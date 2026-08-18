using System;
using System.IO;
using System.Text.Json;

using MultiScrcpy;
using MultiScrcpy.Core;
using MultiScrcpy.Core.Decoder;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 领域模型与配置的无头单测（架构文档 §8 T01-3 / T01-6）。
/// <para>
/// ⚠️ 本文件不触碰任何 FFmpeg P/Invoke：只调用
/// <see cref="FrameConverter.Quantize"/> 这类纯算术静态方法。
/// </para>
/// </summary>
public sealed class ModelTests
{
    // ---------------------------------------------------------------- DeviceInfo

    [Fact]
    public void DeviceInfo_默认值符合约定()
    {
        var info = new DeviceInfo("SN001");

        Assert.Equal("SN001", info.Serial);
        Assert.Equal(DeviceState.Offline, info.State);
        Assert.Equal(string.Empty, info.Model);
        Assert.Equal(-1, info.Battery);          // -1 = 未知，绝不显示为 0%
        Assert.Equal(0, info.VideoWidth);
        Assert.Equal(0, info.VideoHeight);
        Assert.Equal(string.Empty, info.DeviceName);
        Assert.Equal(string.Empty, info.LastError);
    }

    [Fact]
    public void DeviceInfo_序列号为null时抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceInfo(null!));
    }

    [Theory]
    [InlineData(DeviceState.Detected, true)]
    [InlineData(DeviceState.Connecting, true)]
    [InlineData(DeviceState.Streaming, true)]
    [InlineData(DeviceState.Offline, false)]
    [InlineData(DeviceState.Unauthorized, false)]
    [InlineData(DeviceState.Error, false)]
    public void DeviceInfo_在线判定覆盖六态(DeviceState state, bool expected)
    {
        Assert.Equal(expected, new DeviceInfo("SN", state).IsOnline());
    }

    [Theory]
    [InlineData(-1, false)]  // 未知电量不算低电量
    [InlineData(0, true)]
    [InlineData(19, true)]
    [InlineData(20, false)]  // 阈值取「小于 20」
    [InlineData(100, false)]
    public void DeviceInfo_低电量阈值为20(int battery, bool expected)
    {
        var info = new DeviceInfo("SN") { Battery = battery };
        Assert.Equal(expected, info.IsLowBattery());
    }

    [Fact]
    public void DeviceInfo_卡片标题显示型号与序列号且不含IMEI()
    {
        var info = new DeviceInfo("R58M12345XY", DeviceState.Streaming, "Pixel 6 Pro") { Battery = 87 };

        string title = info.DisplayTitle();

        Assert.Equal("Pixel 6 Pro | R58M12345XY | 电量 87%", title);
        Assert.DoesNotContain("IMEI", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceInfo_标题在型号或电量缺失时优雅降级()
    {
        Assert.Equal("未知型号 | SN001", new DeviceInfo("SN001").DisplayTitle());
        Assert.Equal("未知型号 | SN001 | 电量 5%", new DeviceInfo("SN001") { Battery = 5 }.DisplayTitle());
        Assert.Equal("Pixel | SN001", new DeviceInfo("SN001", DeviceState.Detected, "Pixel").DisplayTitle());
    }

    [Fact]
    public void DeviceInfo_合并时保留已有的非空信息()
    {
        var current = new DeviceInfo("SN", DeviceState.Streaming, "Pixel 6") { Battery = 80 };
        var scanned = new DeviceInfo("SN", DeviceState.Detected);  // 扫描结果无型号、电量未知

        current.MergeFrom(scanned);

        // 会话态（Streaming）不得被扫描态（Detected）覆盖；型号 / 电量仍按非空保留。
        Assert.Equal(DeviceState.Streaming, current.State);
        Assert.Equal("Pixel 6", current.Model);             // 型号不被空串抹掉
        Assert.Equal(80, current.Battery);                  // 电量不被 -1 抹掉
    }

    [Fact]
    public void DeviceInfo_合并时新信息覆盖旧信息()
    {
        var current = new DeviceInfo("SN", DeviceState.Detected, "旧型号") { Battery = 80 };
        current.MergeFrom(new DeviceInfo("SN", DeviceState.Streaming, "新型号") { Battery = 42 });

        Assert.Equal(DeviceState.Streaming, current.State);
        Assert.Equal("新型号", current.Model);
        Assert.Equal(42, current.Battery);
    }

    [Fact]
    public void DeviceInfo_合并null不抛异常()
    {
        var info = new DeviceInfo("SN", DeviceState.Streaming, "Pixel");
        info.MergeFrom(null!);
        Assert.Equal(DeviceState.Streaming, info.State);
    }

    // ---------------------------------------------------------------- TunnelHandle

    [Fact]
    public void TunnelHandle_抽象套接字名与scid一致()
    {
        var handle = new TunnelHandle("SN001", "deadbeef", 27183);

        Assert.Equal("scrcpy_deadbeef", handle.AbstractName);
        Assert.Equal(27183, handle.Port);
        Assert.Null(handle.ServerProcess);
    }

    // ---------------------------------------------------------------- AppConfig

    [Fact]
    public void AppConfig_默认值符合PRD()
    {
        var cfg = new AppConfig();

        Assert.Equal("4.0", cfg.ServerVersion);
        Assert.True(cfg.TunnelForward);
        Assert.Equal(27183, cfg.PortBase);
        Assert.Equal("h264", cfg.VideoCodec);
        Assert.Equal(2400, cfg.MaxSize);
        Assert.Equal(8_000_000, cfg.VideoBitRate);
        Assert.Equal(30, cfg.MaxFps);
        Assert.Equal(2000, cfg.ScanIntervalMs);
        Assert.Equal(30000, cfg.StatusIntervalMs);
        Assert.Equal(8, cfg.MaxDevices);          // PRD v1.2 Q1
        Assert.Equal(4, cfg.SwsFlags);            // SWS_BICUBIC（降采样画质修复，原为 2 = SWS_BILINEAR）
        Assert.Equal(240, cfg.CardBaseWidth);     // 长屏适配：原 300，画面区 236x506（r≈0.466）
        Assert.Equal(600, cfg.CardBaseHeight);    // 长屏适配：原 560
        Assert.False(string.IsNullOrWhiteSpace(cfg.ScreenshotDir));
    }

    [Fact]
    public void AppConfig_归一化把越界值收敛回合法范围()
    {
        var cfg = new AppConfig
        {
            MaxDevices = 0,
            PortBase = 80,
            MaxSize = -1,
            VideoBitRate = 0,
            MaxFps = 500,
            ScanIntervalMs = 10,
            StatusIntervalMs = 100,
            AdbTimeoutMs = 1,
            ServerVersion = "  ",
            VideoCodec = "",
            ScreenshotDir = "   ",
            SwsFlags = 0,
            CardBaseWidth = 10,
            CardBaseHeight = 10
        };

        cfg.Normalize();

        Assert.Equal(8, cfg.MaxDevices);
        Assert.Equal(27183, cfg.PortBase);
        Assert.Equal(2400, cfg.MaxSize);
        Assert.Equal(8_000_000, cfg.VideoBitRate);
        Assert.Equal(30, cfg.MaxFps);
        Assert.Equal(500, cfg.ScanIntervalMs);
        Assert.Equal(2000, cfg.StatusIntervalMs);
        Assert.Equal(1000, cfg.AdbTimeoutMs);
        Assert.Equal("4.0", cfg.ServerVersion);
        Assert.Equal("h264", cfg.VideoCodec);
        Assert.Equal(2, cfg.SwsFlags);
        Assert.Equal(240, cfg.CardBaseWidth);     // 长屏适配：非法值回退到新默认 240（原 300）
        Assert.Equal(600, cfg.CardBaseHeight);    // 长屏适配：非法值回退到新默认 600（原 560）
        Assert.False(string.IsNullOrWhiteSpace(cfg.ScreenshotDir));
    }

    [Fact]
    public void AppConfig_归一化对设备数上限封顶()
    {
        var cfg = new AppConfig { MaxDevices = 999 };
        cfg.Normalize();
        Assert.Equal(32, cfg.MaxDevices);
    }

    [Fact]
    public void AppConfig_归一化对合法值幂等()
    {
        var cfg = new AppConfig { MaxDevices = 4, MaxFps = 24, MaxSize = 720 };
        cfg.Normalize();
        cfg.Normalize();

        Assert.Equal(4, cfg.MaxDevices);
        Assert.Equal(24, cfg.MaxFps);
        Assert.Equal(720, cfg.MaxSize);
    }

    [Fact]
    public void AppConfig_JSON往返不丢字段()
    {
        var original = new AppConfig
        {
            AdbPath = @"C:\platform-tools\adb.exe",
            ServerVersion = "4.0",
            MaxDevices = 6,
            MaxFps = 24,
            VideoCodec = "h265",
            ScreenshotDir = @"D:\截图目录"   // 非 ASCII 路径不得被转义成 \uXXXX 乱码
        };

        string path = Path.Combine(Path.GetTempPath(), $"mscp_cfg_{Guid.NewGuid():N}.json");
        try
        {
            original.Save(path);
            Assert.True(File.Exists(path));

            string json = File.ReadAllText(path);
            Assert.Contains(@"D:\\截图目录", json);   // UnsafeRelaxedJsonEscaping 保留中文

            AppConfig loaded = AppConfig.Load(path);

            Assert.Equal(original.AdbPath, loaded.AdbPath);
            Assert.Equal(original.ServerVersion, loaded.ServerVersion);
            Assert.Equal(original.MaxDevices, loaded.MaxDevices);
            Assert.Equal(original.MaxFps, loaded.MaxFps);
            Assert.Equal(original.VideoCodec, loaded.VideoCodec);
            Assert.Equal(original.ScreenshotDir, loaded.ScreenshotDir);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void AppConfig_保存文件不带UTF8_BOM()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mscp_bom_{Guid.NewGuid():N}.json");
        try
        {
            new AppConfig().Save(path);

            byte[] head = File.ReadAllBytes(path);
            Assert.True(head.Length >= 3);
            Assert.False(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF, "配置文件不得带 UTF-8 BOM");
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void AppConfig_载入损坏JSON时回退默认配置而不抛异常()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mscp_bad_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON ");

            AppConfig cfg = AppConfig.Load(path);

            Assert.Equal(8, cfg.MaxDevices);
            Assert.Equal("4.0", cfg.ServerVersion);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void AppConfig_容忍注释与尾随逗号()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mscp_lenient_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  // 同时投屏的设备上限
                  "maxDevices": 3,
                  "maxFps": 15,
                }
                """);

            AppConfig cfg = AppConfig.Load(path);

            Assert.Equal(3, cfg.MaxDevices);
            Assert.Equal(15, cfg.MaxFps);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void AppConfig_属性名大小写不敏感()
    {
        AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>("""{"MAXDEVICES":5,"max_size":720,"MaxSize":640}""",
                                                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(cfg);
        Assert.Equal(5, cfg!.MaxDevices);
        Assert.Equal(640, cfg.MaxSize);
    }

    [Fact]
    public void AppConfig_默认截图目录落在我的图片下的MultiScrcpy()
    {
        string dir = AppConfig.DefaultScreenshotDir();

        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.EndsWith("MultiScrcpy", dir, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(dir));
    }

    [Fact]
    public void AppConfig_解析server_jar路径带版本号()
    {
        var cfg = new AppConfig { ServerVersion = "4.0", ServerJarPath = string.Empty };

        string jar = cfg.ResolveServerJar();

        Assert.EndsWith(Path.Combine("assets", "scrcpy-server-v4.0.jar"), jar, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(jar));
    }

    [Fact]
    public void AppConfig_显式指定的jar路径优先()
    {
        var cfg = new AppConfig { ServerJarPath = @"D:\jars\my-server.jar" };
        Assert.Equal(Path.GetFullPath(@"D:\jars\my-server.jar"), cfg.ResolveServerJar());
    }

    [Fact]
    public void AppConfig_解析FFmpeg目录默认在输出目录下()
    {
        var cfg = new AppConfig { FFmpegPath = string.Empty };

        string dir = cfg.ResolveFFmpegDir();

        Assert.EndsWith(Path.Combine("ffmpeg", "x64"), dir, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(dir));
    }

    [Fact]
    public void AppConfig_指定不存在的adb时抛出可读异常()
    {
        var cfg = new AppConfig { AdbPath = @"Z:\绝对不存在的目录\adb.exe" };

        AdbException ex = Assert.Throws<AdbException>(() => cfg.ResolveAdb());
        Assert.Contains("adb", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 尺寸量化

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    [InlineData(31, 32)]
    [InlineData(32, 32)]
    [InlineData(300, 304)]
    [InlineData(360, 368)]
    [InlineData(1080, 1088)]
    [InlineData(-5, 16)]
    public void FrameConverter_尺寸量化到16的倍数(int input, int expected)
    {
        // 纯算术静态方法：不加载任何 FFmpeg 原生 DLL
        Assert.Equal(expected, FrameConverter.Quantize(input));
    }

    [Fact]
    public void FrameConverter_量化结果恒为16对齐且不小于原值()
    {
        for (int i = -8; i <= 2048; i++)
        {
            int q = FrameConverter.Quantize(i);

            Assert.Equal(0, q % FrameConverter.SizeAlignment);
            Assert.True(q >= FrameConverter.SizeAlignment);
            if (i > 0) Assert.True(q >= i, $"量化后 {q} 小于原始尺寸 {i}");
        }
    }

    [Fact]
    public void FrameConverter_量化幂等()
    {
        foreach (int v in new[] { 16, 32, 304, 368, 1088 })
        {
            Assert.Equal(v, FrameConverter.Quantize(FrameConverter.Quantize(v)));
        }
    }

    // ---------------------------------------------------------------- FrameBuffer

    [Fact]
    public void FrameBuffer_未初始化时取帧为空()
    {
        using var fb = new FrameBuffer();

        Assert.Equal(0, fb.Width);
        Assert.Equal(0, fb.Height);
        Assert.Null(fb.BeginRender());
        Assert.Null(fb.Acquire());
    }

    [Fact]
    public void FrameBuffer_发布后UI侧能取到帧且序号递增()
    {
        using var fb = new FrameBuffer();
        fb.Resize(64, 128);

        Assert.Equal(64, fb.Width);
        Assert.Equal(128, fb.Height);
        Assert.Equal(0, fb.Sequence);

        Assert.NotNull(fb.BeginRender());
        fb.Publish();
        Assert.Equal(1, fb.Sequence);

        var front = fb.Acquire();
        Assert.NotNull(front);
        Assert.Equal(64, front!.Width);
        Assert.Equal(128, front.Height);
    }

    [Fact]
    public void FrameBuffer_无新帧时重复取到同一张位图()
    {
        using var fb = new FrameBuffer();
        fb.Resize(32, 32);
        fb.Publish();

        var a = fb.Acquire();
        var b = fb.Acquire();

        Assert.NotNull(a);
        Assert.Same(a, b);   // latest-frame-wins：没有新帧就复用上一帧，不重新分配
    }

    [Fact]
    public void FrameBuffer_后台与前台位图互不相同()
    {
        using var fb = new FrameBuffer();
        fb.Resize(32, 32);
        fb.Publish();

        var front = fb.Acquire();
        var back = fb.BeginRender();

        Assert.NotNull(front);
        Assert.NotNull(back);
        Assert.NotSame(front, back);   // 同一时刻同一块位图只能被一个线程访问
    }

    [Fact]
    public void FrameBuffer_分辨率变更后取到新尺寸位图()
    {
        using var fb = new FrameBuffer();
        fb.Resize(32, 32);
        fb.Publish();
        Assert.NotNull(fb.Acquire());

        fb.Resize(64, 96);
        fb.Publish();

        var front = fb.Acquire();
        Assert.NotNull(front);
        Assert.Equal(64, front!.Width);
        Assert.Equal(96, front.Height);
    }

    [Fact]
    public void FrameBuffer_同尺寸重复Resize不重建()
    {
        using var fb = new FrameBuffer();
        fb.Resize(48, 48);
        var back1 = fb.BeginRender();

        fb.Resize(48, 48);
        var back2 = fb.BeginRender();

        Assert.Same(back1, back2);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, -1)]
    public void FrameBuffer_非法尺寸被忽略(int w, int h)
    {
        using var fb = new FrameBuffer();
        fb.Resize(w, h);

        Assert.Equal(0, fb.Width);
        Assert.Equal(0, fb.Height);
    }

    [Fact]
    public void FrameBuffer_释放后所有操作安全返回()
    {
        var fb = new FrameBuffer();
        fb.Resize(32, 32);
        fb.Dispose();
        fb.Dispose();   // 幂等

        Assert.Null(fb.BeginRender());
        Assert.Null(fb.Acquire());
        fb.Publish();   // 不得抛异常
        fb.Resize(64, 64);
        Assert.Equal(0, fb.Width);
    }
}
