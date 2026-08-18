using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Decoder;
using MultiScrcpy.Protocol;

namespace MultiScrcpy.Verify;

/// <summary>
/// 解码管线运行时验证程序（headless 控制台）。
/// <para>
/// 覆盖 QA 遗留风险 #1 / #2 —— 这三条路径在无头环境下是可实证的：
/// <list type="number">
///   <item><description><b>V1</b> FFmpeg 原生库注册（架构文档 §6.1）：
///   <c>FFmpegBinariesHelper.Register()</c> 成功、<c>av_version_info()</c> 非空。</description></item>
///   <item><description><b>V2</b> 增量解码（§6.2 / §6.3）：<c>H264Decoder</c> 以 scrcpy 语义
///   （config 包缓存 + 与 IDR 前置合并、关键帧门禁、64 字节 padding）逐包喂流并出帧。</description></item>
///   <item><description><b>V3</b> 帧转换 / GDI 渲染源（§6.4）：<c>FrameConverter</c> 用
///   <c>sws_scale</c> 把 YUV420P 零拷贝写入 <c>Format24bppRgb</c> 的 <see cref="Bitmap"/>，
///   像素内容有效（非全黑、有色彩层次）。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>不覆盖</b>：adb 握手 / 多设备并发 / touch 注入 / 旋转 / 退出清理 —— 这些必须有真机。
/// </para>
/// <para>用法：<c>MultiScrcpy.Verify.exe [--input sample.h264] [--size 640x480] [--frames 10] [--keep]</c></para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;

    /// <summary>默认样本参数。</summary>
    private const int DefaultWidth = 640;

    private const int DefaultHeight = 480;
    private const int DefaultFrames = 10;
    private const int DefaultFps = 10;

    /// <summary>判定「首帧非空」的最低有效像素占比。</summary>
    private const double MinNonBlackRatio = 0.50;

    /// <summary>判定「首帧有色彩层次」的最低不同颜色数。</summary>
    private const int MinDistinctColors = 8;

    private static int Main(string[] args)
    {
        TrySetUtf8Console();
        Log.Setup(minimumLevel: LogLevel.Info);

        var options = Options.Parse(args ?? Array.Empty<string>());
        string outputDir = Path.Combine(AppContext.BaseDirectory, "verify-out");
        Directory.CreateDirectory(outputDir);

        Header("MultiScrcpyPanel 解码管线运行时验证");
        Console.WriteLine($"  程序目录 : {AppContext.BaseDirectory}");
        Console.WriteLine($"  产物目录 : {outputDir}");
        Console.WriteLine($"  运行环境 : {Environment.OSVersion} / .NET {Environment.Version} / " +
                          $"{(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine();

        var results = new List<CheckResult>();

        try
        {
            string version = StepRegisterFFmpeg(results);
            string samplePath = StepPrepareSample(results, options, outputDir);
            DecodeReport report = StepDecode(results, samplePath, options, outputDir);

            Header("汇总");
            Console.WriteLine($"  FFmpeg 版本       : {version}");
            Console.WriteLine($"  原生库目录        : {FFmpegBinariesHelper.NativeDirectory}");
            Console.WriteLine($"  样本文件          : {samplePath}");
            Console.WriteLine($"  喂入媒体包        : {report.PacketCount}（其中 config {report.ConfigPacketCount}、" +
                              $"关键帧 {report.KeyFramePacketCount}）");
            Console.WriteLine($"  成功解码帧数      : {report.DecodedFrames}");
            Console.WriteLine($"  首帧位图尺寸      : {report.FirstBitmapWidth}x{report.FirstBitmapHeight}");
            Console.WriteLine($"  首帧非黑像素占比  : {report.FirstNonBlackRatio:P2}");
            Console.WriteLine($"  首帧不同颜色数    : {report.FirstDistinctColors}");
            Console.WriteLine($"  缩放路径位图尺寸  : {report.ScaledBitmapWidth}x{report.ScaledBitmapHeight}");
            Console.WriteLine($"  解码耗时          : {report.ElapsedMs} ms");
            Console.WriteLine($"  首帧 PNG          : {report.FirstFramePngPath}");
            Console.WriteLine($"  缩放帧 PNG        : {report.ScaledFramePngPath}");
        }
        catch (Exception ex)
        {
            results.Add(CheckResult.Fail("运行时验证", ex.Message));
            Console.Error.WriteLine();
            Console.Error.WriteLine("验证过程中断：");
            Console.Error.WriteLine(ex);
        }

        return Report(results);
    }

    // ------------------------------------------------------------------ V1

    /// <summary>V1：注册 FFmpeg 原生库并读取版本。</summary>
    private static string StepRegisterFFmpeg(List<CheckResult> results)
    {
        Header("V1  FFmpeg 原生库注册（架构文档 §6.1）");

        string expected = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "x64");
        Console.WriteLine($"  期望目录 : {expected}");
        Console.WriteLine($"  目录存在 : {Directory.Exists(expected)}");

        if (Directory.Exists(expected))
        {
            foreach (string dll in Directory.GetFiles(expected, "*.dll"))
            {
                var info = new FileInfo(dll);
                Console.WriteLine($"    - {info.Name,-22} {info.Length / 1024.0 / 1024.0,6:F2} MB");
            }
        }

        FFmpegBinariesHelper.Register();

        results.Add(FFmpegBinariesHelper.IsRegistered
            ? CheckResult.Pass("V1-1 Register() 成功，IsRegistered == true")
            : CheckResult.Fail("V1-1 Register()", "IsRegistered 仍为 false"));

        string version = FFmpegBinariesHelper.VersionInfo();
        Console.WriteLine($"  av_version_info() = \"{version}\"");

        bool versionOk = !string.IsNullOrWhiteSpace(version)
                         && !string.Equals(version, "未加载", StringComparison.Ordinal)
                         && !string.Equals(version, "未知", StringComparison.Ordinal);
        results.Add(versionOk
            ? CheckResult.Pass($"V1-2 av_version_info() 非空：{version}")
            : CheckResult.Fail("V1-2 av_version_info()", $"返回占位值 \"{version}\""));

        bool major6 = version.StartsWith("6.", StringComparison.Ordinal);
        results.Add(major6
            ? CheckResult.Pass("V1-3 主版本为 6.x，与 FFmpeg.AutoGen 6.0.0.2 配对正确")
            : CheckResult.Fail("V1-3 版本配对",
                $"原生库版本 {version} 不是 6.x，违反架构文档 §6.1 版本配对铁律"));

        // V1-4 复制项断言：确保 Register 用的是本地 ffmpeg\x64，而非 PATH 上碰巧存在的 DLL
        string nativeDir = FFmpegBinariesHelper.NativeDirectory;
        bool localNative = !string.IsNullOrWhiteSpace(nativeDir)
                           && nativeDir.Contains("ffmpeg" + Path.DirectorySeparatorChar + "x64", StringComparison.OrdinalIgnoreCase);
        results.Add(localNative
            ? CheckResult.Pass($"V1-4 原生库目录指向本地 ffmpeg\\x64：{nativeDir}")
            : CheckResult.Fail("V1-4 原生库目录",
                $"NativeDirectory=\"{nativeDir}\" 未指向本地 ffmpeg\\x64，复制项可能失效（被 PATH 兜底掩盖）"));

        return version;
    }

    // ------------------------------------------------------------------ 样本

    /// <summary>准备离线 H.264 Annex-B 样本：优先用 --input，否则用 ffmpeg.exe 现生成。</summary>
    private static string StepPrepareSample(List<CheckResult> results, Options options, string outputDir)
    {
        Header("V0  准备离线 H.264 Annex-B 样本");

        if (!string.IsNullOrWhiteSpace(options.InputPath))
        {
            string given = Path.GetFullPath(options.InputPath!);
            if (!File.Exists(given))
            {
                throw new FileNotFoundException($"--input 指定的样本不存在：{given}", given);
            }

            Console.WriteLine($"  使用外部样本 : {given}（{new FileInfo(given).Length} 字节）");
            results.Add(CheckResult.Pass($"V0-1 使用外部样本 {Path.GetFileName(given)}"));
            return given;
        }

        string samplePath = Path.Combine(outputDir, "sample.h264");
        if (File.Exists(samplePath) && options.Keep && new FileInfo(samplePath).Length > 0)
        {
            Console.WriteLine($"  复用既有样本 : {samplePath}（{new FileInfo(samplePath).Length} 字节）");
            results.Add(CheckResult.Pass("V0-1 复用既有样本"));
            return samplePath;
        }

        string? ffmpegExe = SampleGenerator.LocateFFmpegExe(AppContext.BaseDirectory);
        if (ffmpegExe == null)
        {
            throw new FileNotFoundException(
                "未找到 ffmpeg.exe（用于生成样本）。请用 --input 显式指定一个 .h264 Annex-B 裸流，" +
                "或把官方 shared 发行包解压到 csharp\\native\\ffmpeg\\ 下。");
        }

        Console.WriteLine($"  ffmpeg.exe   : {ffmpegExe}");
        Console.WriteLine($"  CLI 版本     : {SampleGenerator.ReadCliVersion(ffmpegExe)}");
        Console.WriteLine($"  生成参数     : testsrc {options.Width}x{options.Height} @ {DefaultFps}fps × {options.Frames} 帧, libx264, yuv420p, bf=0, g=5");

        SampleGenerator.Generate(ffmpegExe, samplePath, options.Width, options.Height, options.Frames, DefaultFps);

        long size = new FileInfo(samplePath).Length;
        Console.WriteLine($"  样本文件     : {samplePath}（{size} 字节）");

        results.Add(size > 0
            ? CheckResult.Pass($"V0-1 样本生成成功，{size} 字节")
            : CheckResult.Fail("V0-1 样本生成", "产物为 0 字节"));

        return samplePath;
    }

    // ------------------------------------------------------------------ V2 / V3

    /// <summary>V2 + V3：增量喂流解码，并把每帧转成 GDI+ 位图。</summary>
    private static DecodeReport StepDecode(List<CheckResult> results, string samplePath,
                                           Options options, string outputDir)
    {
        Header("V2  H264Decoder 增量喂流（架构文档 §6.2 / §6.3）");

        byte[] raw = File.ReadAllBytes(samplePath);
        List<MediaPacket> packets = AnnexBReader.ToScrcpyPackets(raw);

        int configCount = 0;
        int keyCount = 0;
        foreach (MediaPacket packet in packets)
        {
            if (packet.IsConfig)
            {
                configCount++;
            }
            else if (packet.IsKeyFrame)
            {
                keyCount++;
            }
        }

        Console.WriteLine($"  裸流字节数   : {raw.Length}");
        Console.WriteLine($"  切分媒体包   : {packets.Count}（config {configCount}、关键帧 {keyCount}、" +
                          $"非关键帧 {packets.Count - configCount - keyCount}）");

        results.Add(packets.Count > 0
            ? CheckResult.Pass($"V2-1 Annex-B 切分出 {packets.Count} 个 scrcpy 语义媒体包")
            : CheckResult.Fail("V2-1 Annex-B 切分", "未切出任何媒体包"));

        results.Add(configCount > 0
            ? CheckResult.Pass($"V2-2 识别出 {configCount} 个 config 包（SPS/PPS）")
            : CheckResult.Fail("V2-2 config 包", "样本中未发现 SPS/PPS，无法验证前置合并逻辑"));

        var report = new DecodeReport
        {
            PacketCount = packets.Count,
            ConfigPacketCount = configCount,
            KeyFramePacketCount = keyCount
        };

        using var decoder = new H264Decoder();
        decoder.Open(ScrcpyConstants.CODEC_H264);

        results.Add(decoder.IsOpen
            ? CheckResult.Pass("V2-3 avcodec_open2 成功（low_delay + FF_THREAD_SLICE ×2）")
            : CheckResult.Fail("V2-3 解码器打开", "IsOpen == false"));

        // 缩放路径：目标尺寸按 16 对齐量化，完整走一遍 UI 卡片的渲染链路
        int scaledW = FrameConverter.Quantize(options.Width / 2);
        int scaledH = FrameConverter.Quantize(options.Height / 2);
        using var scaler = new FrameConverter(scaledW, scaledH);
        using var scaledBitmap = new Bitmap(scaledW, scaledH, PixelFormat.Format24bppRgb);

        Bitmap? firstFrame = null;
        bool scaledOk = false;
        int decoded = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            foreach (MediaPacket packet in packets)
            {
                decoder.TryDecode(packet, framePtr =>
                {
                    decoded++;

                    if (firstFrame == null)
                    {
                        // 源分辨率位图（截图路径，PRD R-P1-4）
                        firstFrame = FrameConverter.ConvertToNewBitmap(framePtr);
                    }

                    // 缩放位图（UI 卡片渲染路径，§6.4）
                    scaler.Convert(framePtr, scaledBitmap);
                    scaledOk = true;
                });
            }

            sw.Stop();
            report.ElapsedMs = sw.ElapsedMilliseconds;
            report.DecodedFrames = decoded;

            Console.WriteLine($"  成功解码帧数 : {decoded}（解码器内部计数 {decoder.DecodedFrameCount}）");
            Console.WriteLine($"  解码耗时     : {report.ElapsedMs} ms");

            // 自生成样本帧数已知(options.Frames)；外部 --input 样本帧数未知，仅要求 > 0
            if (string.IsNullOrWhiteSpace(options.InputPath))
            {
                results.Add(decoded == options.Frames
                    ? CheckResult.Pass($"V2-4 增量喂流解出 {decoded} 帧 == 预期 {options.Frames}（config 前置合并 + 关键帧门禁生效）")
                    : CheckResult.Fail("V2-4 增量解码", $"解出 {decoded} 帧，预期 {options.Frames} 帧"));
            }
            else
            {
                results.Add(decoded > 0
                    ? CheckResult.Pass($"V2-4 增量喂流解出 {decoded} 帧（外部样本）")
                    : CheckResult.Fail("V2-4 增量解码", "一帧都没解出来"));
            }

            results.Add(decoder.DecodedFrameCount == decoded
                ? CheckResult.Pass($"V2-5 DecodedFrameCount 与回调次数一致（{decoded}）")
                : CheckResult.Fail("V2-5 帧计数一致性",
                    $"DecodedFrameCount={decoder.DecodedFrameCount} != 回调次数 {decoded}"));

            // -------------------------------------------------------- V3
            Header("V3  FrameConverter → GDI+ Bitmap（架构文档 §6.4）");

            if (firstFrame == null)
            {
                results.Add(CheckResult.Fail("V3-1 首帧位图", "没有可用的解码帧"));
                return report;
            }

            report.FirstBitmapWidth = firstFrame.Width;
            report.FirstBitmapHeight = firstFrame.Height;

            Console.WriteLine($"  首帧位图     : {firstFrame.Width}x{firstFrame.Height} / {firstFrame.PixelFormat}");

            bool sizeOk = firstFrame.Width == options.Width && firstFrame.Height == options.Height;
            results.Add(sizeOk
                ? CheckResult.Pass($"V3-1 首帧位图尺寸 == 源分辨率 {options.Width}x{options.Height}")
                : CheckResult.Fail("V3-1 首帧位图尺寸",
                    $"期望 {options.Width}x{options.Height}，实际 {firstFrame.Width}x{firstFrame.Height}"));

            results.Add(firstFrame.PixelFormat == PixelFormat.Format24bppRgb
                ? CheckResult.Pass("V3-2 首帧像素格式为 Format24bppRgb（匹配 sws BGR24）")
                : CheckResult.Fail("V3-2 首帧像素格式", firstFrame.PixelFormat.ToString()));

            PixelStats stats = Analyze(firstFrame);
            report.FirstNonBlackRatio = stats.NonBlackRatio;
            report.FirstDistinctColors = stats.DistinctColors;

            Console.WriteLine($"  非黑像素占比 : {stats.NonBlackRatio:P2}（{stats.NonBlackPixels}/{stats.TotalPixels}）");
            Console.WriteLine($"  不同颜色数   : {stats.DistinctColors}");
            Console.WriteLine($"  左上角像素   : {stats.TopLeft}");
            Console.WriteLine($"  中心像素     : {stats.Center}");

            results.Add(stats.NonBlackRatio >= MinNonBlackRatio
                ? CheckResult.Pass($"V3-3 首帧像素非空，非黑占比 {stats.NonBlackRatio:P2} ≥ {MinNonBlackRatio:P0}")
                : CheckResult.Fail("V3-3 首帧像素非空",
                    $"非黑占比仅 {stats.NonBlackRatio:P2}，疑似全黑（sws_scale 未真正写入）"));

            results.Add(stats.DistinctColors >= MinDistinctColors
                ? CheckResult.Pass($"V3-4 首帧有色彩层次，{stats.DistinctColors} 种颜色 ≥ {MinDistinctColors}")
                : CheckResult.Fail("V3-4 首帧色彩层次", $"仅 {stats.DistinctColors} 种颜色"));

            // V3-4b 通道序断言：仅自生成默认样本(testsrc 640x480)下做确定性采样。
            // 实测已知两点：中心(320,240)为黄条 R≈255/G≈255/B≈0； (137,30)为青条 R≈0/G≈255/B≈255。
            // 若 sws_scale 把 R/B 写反，两点颜色互换（黄变青、青变黄），断言同时失败 → 拦截通道序错误。
            if (string.IsNullOrWhiteSpace(options.InputPath)
                && options.Width == DefaultWidth && options.Height == DefaultHeight)
            {
                (byte yr, byte yg, byte yb) = SamplePixel(firstFrame!, 320, 240);
                (byte cr, byte cg, byte cb) = SamplePixel(firstFrame!, 137, 30);
                bool yellowOk = yr > yb + 100;   // 黄条：R 远高于 B
                bool cyanOk  = cb > cr + 100;    // 青条：B 远高于 R
                results.Add(yellowOk && cyanOk
                    ? CheckResult.Pass($"V3-4b 颜色通道序正确（黄条 R={yr} B={yb}，青条 R={cr} B={cb}）")
                    : CheckResult.Fail("V3-4b 颜色通道序",
                        $"黄条 R={yr} B={yb} / 青条 R={cr} B={cb} 疑似 R/B 写反"));
            }

            report.FirstFramePngPath = Path.Combine(outputDir, "frame-000-source.png");
            firstFrame.Save(report.FirstFramePngPath, ImageFormat.Png);
            Console.WriteLine($"  已保存 PNG   : {report.FirstFramePngPath}");

            results.Add(File.Exists(report.FirstFramePngPath) && new FileInfo(report.FirstFramePngPath).Length > 0
                ? CheckResult.Pass("V3-5 Bitmap.Save(PNG) 成功（System.Drawing 在无头 Windows 下可用）")
                : CheckResult.Fail("V3-5 Bitmap.Save", "PNG 未写出或为 0 字节"));

            // 缩放路径断言
            report.ScaledBitmapWidth = scaledBitmap.Width;
            report.ScaledBitmapHeight = scaledBitmap.Height;

            results.Add(scaledOk
                ? CheckResult.Pass($"V3-6 缩放路径 sws_scale 成功写入 {scaledW}x{scaledH} 位图（Quantize 对齐 16）")
                : CheckResult.Fail("V3-6 缩放路径", "FrameConverter.Convert 从未成功执行"));

            PixelStats scaledStats = Analyze(scaledBitmap);
            Console.WriteLine($"  缩放位图     : {scaledW}x{scaledH}，非黑占比 {scaledStats.NonBlackRatio:P2}，" +
                              $"{scaledStats.DistinctColors} 种颜色");

            results.Add(scaledStats.NonBlackRatio >= MinNonBlackRatio
                ? CheckResult.Pass($"V3-7 缩放位图像素非空，非黑占比 {scaledStats.NonBlackRatio:P2}")
                : CheckResult.Fail("V3-7 缩放位图像素非空", $"非黑占比仅 {scaledStats.NonBlackRatio:P2}"));

            report.ScaledFramePngPath = Path.Combine(outputDir, "frame-last-scaled.png");
            scaledBitmap.Save(report.ScaledFramePngPath, ImageFormat.Png);
            Console.WriteLine($"  已保存 PNG   : {report.ScaledFramePngPath}");

            return report;
        }
        finally
        {
            firstFrame?.Dispose();
        }
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>读取位图像素并统计非黑占比 / 颜色数（不使用 unsafe）。</summary>
    private static PixelStats Analyze(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData bd = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(bd.Stride);
            int total = stride * bd.Height;
            byte[] buffer = new byte[total];
            Marshal.Copy(bd.Scan0, buffer, 0, total);

            long nonBlack = 0;
            var distinct = new HashSet<int>();
            string topLeft = "n/a";
            string center = "n/a";

            for (int y = 0; y < bd.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bd.Width; x++)
                {
                    int i = row + (x * 3);
                    byte b = buffer[i];
                    byte g = buffer[i + 1];
                    byte r = buffer[i + 2];

                    if ((b | g | r) != 0)
                    {
                        nonBlack++;
                    }

                    if (distinct.Count < 8192)
                    {
                        distinct.Add((r << 16) | (g << 8) | b);
                    }

                    if (x == 0 && y == 0)
                    {
                        topLeft = FormatRgb(r, g, b);
                    }

                    if (x == bd.Width / 2 && y == bd.Height / 2)
                    {
                        center = FormatRgb(r, g, b);
                    }
                }
            }

            long totalPixels = (long)bd.Width * bd.Height;
            return new PixelStats(nonBlack, totalPixels, distinct.Count, topLeft, center);
        }
        finally
        {
            bitmap.UnlockBits(bd);
        }
    }

    private static string FormatRgb(byte r, byte g, byte b) =>
        string.Format(CultureInfo.InvariantCulture, "R={0,3} G={1,3} B={2,3}", r, g, b);

    private static (byte R, byte G, byte B) SamplePixel(Bitmap bitmap, int x, int y)
    {
        var rect = new Rectangle(x, y, 1, 1);
        BitmapData bd = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] buf = new byte[Math.Abs(bd.Stride)];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            return (buf[2], buf[1], buf[0]);
        }
        finally
        {
            bitmap.UnlockBits(bd);
        }
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 72));
    }

    /// <summary>打印检查清单并返回进程退出码。</summary>
    private static int Report(List<CheckResult> results)
    {
        Header("检查清单");

        int passed = 0;
        foreach (CheckResult result in results)
        {
            Console.WriteLine(result.Ok
                ? $"  [PASS] {result.Message}"
                : $"  [FAIL] {result.Message}");
            if (result.Ok)
            {
                passed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {passed}/{results.Count} 项通过。");
        Console.WriteLine();

        bool allOk = results.Count > 0 && passed == results.Count;
        Console.WriteLine(allOk
            ? "RUNTIME_VERIFY: PASS —— FFmpeg 解码 + 帧转换（GDI 渲染源）运行时路径已实证可用。"
            : "RUNTIME_VERIFY: FAIL —— 存在未通过项，详见上方 [FAIL] 行。");
        Console.WriteLine("注意：adb 握手 / 多设备并发 / touch 注入 / 旋转 / 退出清理 需要真机，本程序不覆盖。");

        return allOk ? ExitOk : ExitFail;
    }

    private static void TrySetUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 输出被重定向到不支持编码切换的管道时忽略
        }
    }

    // ------------------------------------------------------------------ 类型

    /// <summary>单项检查结论。</summary>
    private readonly record struct CheckResult(bool Ok, string Message)
    {
        public static CheckResult Pass(string message) => new(true, message);

        public static CheckResult Fail(string item, string reason) => new(false, $"{item}：{reason}");
    }

    /// <summary>位图像素统计。</summary>
    private readonly record struct PixelStats(
        long NonBlackPixels, long TotalPixels, int DistinctColors, string TopLeft, string Center)
    {
        public double NonBlackRatio => TotalPixels == 0 ? 0d : (double)NonBlackPixels / TotalPixels;
    }

    /// <summary>解码结果汇总。</summary>
    private sealed class DecodeReport
    {
        public int PacketCount { get; set; }

        public int ConfigPacketCount { get; set; }

        public int KeyFramePacketCount { get; set; }

        public int DecodedFrames { get; set; }

        public long ElapsedMs { get; set; }

        public int FirstBitmapWidth { get; set; }

        public int FirstBitmapHeight { get; set; }

        public double FirstNonBlackRatio { get; set; }

        public int FirstDistinctColors { get; set; }

        public int ScaledBitmapWidth { get; set; }

        public int ScaledBitmapHeight { get; set; }

        public string FirstFramePngPath { get; set; } = string.Empty;

        public string ScaledFramePngPath { get; set; } = string.Empty;
    }

    /// <summary>命令行选项。</summary>
    private sealed class Options
    {
        public string? InputPath { get; private set; }

        public int Width { get; private set; } = DefaultWidth;

        public int Height { get; private set; } = DefaultHeight;

        public int Frames { get; private set; } = DefaultFrames;

        public bool Keep { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--input" when i + 1 < args.Length:
                        options.InputPath = args[++i];
                        break;

                    case "--size" when i + 1 < args.Length:
                        {
                            string[] parts = args[++i].Split('x', 'X');
                            if (parts.Length == 2
                                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
                                && w > 0 && h > 0)
                            {
                                options.Width = w;
                                options.Height = h;
                            }

                            break;
                        }

                    case "--frames" when i + 1 < args.Length:
                        if (int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
                        {
                            options.Frames = n;
                        }

                        break;

                    case "--keep":
                        options.Keep = true;
                        break;

                    default:
                        // 未知参数忽略，保持验证程序易用
                        break;
                }
            }

            return options;
        }
    }
}
