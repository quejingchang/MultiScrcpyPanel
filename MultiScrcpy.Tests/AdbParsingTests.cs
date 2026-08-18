using System;
using System.Collections.Generic;
using System.Linq;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// adb 输出解析与 server 命令拼装的无头单测（架构文档 §8 T02）。
/// <para>
/// 全部只调用 <b>纯函数</b> 或不启动子进程的构造逻辑：不依赖真机、不依赖 adb.exe 存在。
/// </para>
/// </summary>
public sealed class AdbParsingTests
{
    // ---------------------------------------------------------------- devices -l

    private const string TypicalDevicesOutput = """
        List of devices attached
        * daemon not running; starting now at tcp:5037 *
        * daemon started successfully *
        1234abcd               device product:raven model:Pixel_6_Pro device:raven transport_id:1
        emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa
        R58M12345XY            unauthorized usb:1-3
        0987zyxw               offline

        """;

    [Fact]
    public void ParseDevicesOutput_解析出全部设备且顺序保持()
    {
        IReadOnlyList<DeviceInfo> devices = AdbClient.ParseDevicesOutput(TypicalDevicesOutput);

        Assert.Equal(4, devices.Count);
        Assert.Equal(new[] { "1234abcd", "emulator-5554", "R58M12345XY", "0987zyxw" },
                     devices.Select(d => d.Serial).ToArray());
    }

    [Fact]
    public void ParseDevicesOutput_状态映射正确且未授权不与离线合并()
    {
        var map = AdbClient.ParseDevicesOutput(TypicalDevicesOutput).ToDictionary(d => d.Serial, d => d.State);

        Assert.Equal(DeviceState.Detected, map["1234abcd"]);
        Assert.Equal(DeviceState.Detected, map["emulator-5554"]);
        Assert.Equal(DeviceState.Unauthorized, map["R58M12345XY"]);
        Assert.Equal(DeviceState.Offline, map["0987zyxw"]);

        // Unauthorized 必须是独立状态：合并进 Offline 会让「重试授权」入口失效
        Assert.NotEqual(DeviceState.Offline, map["R58M12345XY"]);
    }

    [Fact]
    public void ParseDevicesOutput_型号取自model字段且下划线还原为空格()
    {
        var map = AdbClient.ParseDevicesOutput(TypicalDevicesOutput).ToDictionary(d => d.Serial, d => d.Model);

        Assert.Equal("Pixel 6 Pro", map["1234abcd"]);
        Assert.Equal("sdk gphone64 x86 64", map["emulator-5554"]);
        Assert.Equal(string.Empty, map["0987zyxw"]);       // 无 model 字段
    }

    [Fact]
    public void ParseDevicesOutput_忽略标题行守护进程噪声行与空行()
    {
        const string noiseOnly = """
            List of devices attached
            * daemon not running; starting now at tcp:5037 *
            * daemon started successfully *
            adb server version (41) doesn't match this client (39); killing...


            """;

        Assert.Empty(AdbClient.ParseDevicesOutput(noiseOnly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void ParseDevicesOutput_空输入返回空列表而非抛异常(string? input)
    {
        Assert.Empty(AdbClient.ParseDevicesOutput(input));
    }

    [Fact]
    public void ParseDevicesOutput_兼容CRLF与制表符分隔()
    {
        const string crlf = "List of devices attached\r\n1234abcd\tdevice\r\nR58M\tunauthorized\r\n";

        IReadOnlyList<DeviceInfo> devices = AdbClient.ParseDevicesOutput(crlf);

        Assert.Equal(2, devices.Count);
        Assert.Equal(DeviceState.Detected, devices[0].State);
        Assert.Equal(DeviceState.Unauthorized, devices[1].State);
    }

    [Fact]
    public void ParseDevicesOutput_状态大小写不敏感()
    {
        IReadOnlyList<DeviceInfo> devices = AdbClient.ParseDevicesOutput("AAA DEVICE\nBBB Unauthorized\n");

        Assert.Equal(DeviceState.Detected, devices[0].State);
        Assert.Equal(DeviceState.Unauthorized, devices[1].State);
    }

    [Fact]
    public void ParseDevicesOutput_未知状态一律归为离线()
    {
        IReadOnlyList<DeviceInfo> devices = AdbClient.ParseDevicesOutput("AAA sideload\nBBB recovery\nCCC bootloader\n");

        Assert.All(devices, d => Assert.Equal(DeviceState.Offline, d.State));
    }

    // ---------------------------------------------------------------- dumpsys battery

    private const string TypicalBatteryOutput = """
        Current Battery Service state:
          AC powered: false
          USB powered: true
          status: 2
          health: 2
          present: true
          level: 87
          scale: 100
          voltage: 4231
          temperature: 298
        """;

    [Fact]
    public void ParseBatteryLevel_解析典型输出()
    {
        Assert.Equal(87, AdbClient.ParseBatteryLevel(TypicalBatteryOutput));
    }

    [Theory]
    [InlineData("  level: 0", 0)]
    [InlineData("  level: 100", 100)]
    [InlineData("level:5", 5)]
    [InlineData("  LEVEL: 42", 42)]
    public void ParseBatteryLevel_容忍缩进与大小写(string line, int expected)
    {
        Assert.Equal(expected, AdbClient.ParseBatteryLevel(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scale: 100\nvoltage: 4231")]  // 没有 level 行
    [InlineData("  level: abc")]               // 非数字
    [InlineData("  level: 101")]               // 越界
    [InlineData("  level: -1")]                // 越界
    public void ParseBatteryLevel_解析失败一律返回负一(string? input)
    {
        Assert.Equal(-1, AdbClient.ParseBatteryLevel(input));
    }

    [Fact]
    public void ParseBatteryLevel_只取第一条合法level行()
    {
        Assert.Equal(60, AdbClient.ParseBatteryLevel("level: 60\nlevel: 20\n"));
    }

    // ---------------------------------------------------------------- server 命令

    private static ScrcpyServerLauncher NewLauncher(Action<AppConfig>? tweak = null)
    {
        var cfg = new AppConfig();
        tweak?.Invoke(cfg);
        // AdbClient 构造不会启动任何进程，此处仅用于满足依赖
        return new ScrcpyServerLauncher(new AdbClient("adb"), cfg);
    }

    [Fact]
    public void BuildServerCommand_逐字对齐架构文档默认配置()
    {
        string cmd = NewLauncher().BuildServerCommand("deadbeef");

        const string expected =
            "CLASSPATH=/data/local/tmp/scrcpy-server.jar " +
            "app_process / com.genymobile.scrcpy.Server " +
            "4.0 " +
            "scid=deadbeef " +
            "log_level=info " +
            "video=true audio=false control=true " +
            "tunnel_forward=true " +
            "video_codec=h264 " +
            "max_size=2400 " +
            "video_bit_rate=8000000 " +
            "max_fps=30 " +
            "cleanup=true";

        Assert.Equal(expected, cmd);
    }

    [Fact]
    public void BuildServerCommand_版本号必须是第一个位置参数()
    {
        string cmd = NewLauncher(c => c.ServerVersion = "4.0").BuildServerCommand("00000001");

        int serverIndex = cmd.IndexOf("com.genymobile.scrcpy.Server", StringComparison.Ordinal);
        int versionIndex = cmd.IndexOf(" 4.0 ", StringComparison.Ordinal);
        int scidIndex = cmd.IndexOf("scid=", StringComparison.Ordinal);

        Assert.True(serverIndex > 0);
        Assert.True(versionIndex > serverIndex, "版本号必须紧跟在 Server 类名之后");
        Assert.True(scidIndex > versionIndex, "scid 必须排在版本号之后");
    }

    [Fact]
    public void BuildServerCommand_forward隧道标志不可省略()
    {
        Assert.Contains("tunnel_forward=true", NewLauncher(c => c.TunnelForward = true).BuildServerCommand("aa"));
        Assert.Contains("tunnel_forward=false", NewLauncher(c => c.TunnelForward = false).BuildServerCommand("aa"));
    }

    [Fact]
    public void BuildServerCommand_音频恒关闭且控制恒开启()
    {
        string cmd = NewLauncher().BuildServerCommand("aa");

        Assert.Contains("video=true", cmd);
        Assert.Contains("audio=false", cmd);   // P0 不做音频
        Assert.Contains("control=true", cmd);  // 触控/按键依赖控制通道
        Assert.Contains("cleanup=true", cmd);  // 退出后设备端自动清理
    }

    [Fact]
    public void BuildServerCommand_编码参数随配置变化()
    {
        string cmd = NewLauncher(c =>
        {
            c.VideoCodec = "h265";
            c.MaxSize = 720;
            c.VideoBitRate = 2_000_000;
            c.MaxFps = 60;
        }).BuildServerCommand("cafebabe");

        Assert.Contains("video_codec=h265", cmd);
        Assert.Contains("max_size=720", cmd);
        Assert.Contains("video_bit_rate=2000000", cmd);
        Assert.Contains("max_fps=60", cmd);
        Assert.Contains("scid=cafebabe", cmd);
    }

    [Fact]
    public void RemoteJarPath为设备端固定路径()
    {
        Assert.Equal("/data/local/tmp/scrcpy-server.jar", ScrcpyServerLauncher.RemoteJarPath);
    }

    // ---------------------------------------------------------------- 端口分配

    [Fact]
    public void PortAllocator_连续申请不重复且可归还复用()
    {
        var allocator = new PortAllocator(41000);

        int a = allocator.Acquire();
        int b = allocator.Acquire();
        int c = allocator.Acquire();

        Assert.NotEqual(a, b);
        Assert.NotEqual(b, c);
        Assert.NotEqual(a, c);
        Assert.Equal(3, allocator.UsedCount);
        Assert.All(new[] { a, b, c }, p => Assert.InRange(p, 41000, 41199));

        allocator.Release(b);
        Assert.Equal(2, allocator.UsedCount);

        int d = allocator.Acquire();
        Assert.Equal(b, d);   // 归还的端口应被优先复用（从基准端口起递增探测）

        allocator.Release(a);
        allocator.Release(c);
        allocator.Release(d);
        Assert.Equal(0, allocator.UsedCount);
    }

    [Fact]
    public void PortAllocator_归还未分配端口是幂等的()
    {
        var allocator = new PortAllocator(41500);

        allocator.Release(41500);
        allocator.Release(41500);
        Assert.Equal(0, allocator.UsedCount);

        int p = allocator.Acquire();
        allocator.Release(p);
        allocator.Release(p);
        Assert.Equal(0, allocator.UsedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(1024)]
    [InlineData(60000)]
    [InlineData(70000)]
    public void PortAllocator_非法基准端口回退到默认值(int basePort)
    {
        var allocator = new PortAllocator(basePort);

        int p = allocator.Acquire();
        Assert.InRange(p, 27183, 27382);
        allocator.Release(p);
    }
}
