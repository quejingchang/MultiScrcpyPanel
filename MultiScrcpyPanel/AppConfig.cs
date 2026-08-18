using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MultiScrcpy.Core;

namespace MultiScrcpy;

/// <summary>
/// 应用配置（架构文档 §8 T01-3）。使用 <c>System.Text.Json</c> 读写 <c>config/settings.json</c>（UTF-8 无 BOM）。
/// </summary>
public sealed record class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>adb.exe 路径；为空则从 PATH 查找。</summary>
    public string AdbPath { get; set; } = string.Empty;

    /// <summary>scrcpy-server jar 路径；为空则用 <c>{BaseDirectory}\assets\scrcpy-server-v4.0.jar</c>。</summary>
    public string ServerJarPath { get; set; } = string.Empty;

    /// <summary>server 版本号，必须与 jar 严格一致，作为第一个位置参数传给 app_process。</summary>
    public string ServerVersion { get; set; } = "4.0";

    /// <summary>隧道模式：true = forward（默认，PC 主动 connect）。reverse 为 P2 预留位。</summary>
    public bool TunnelForward { get; set; } = true;

    /// <summary>本地端口起始值，递增分配。</summary>
    public int PortBase { get; set; } = 27183;

    /// <summary>视频编码格式。</summary>
    public string VideoCodec { get; set; } = "h264";

    /// <summary>server 侧最大边长（较长边，单位像素）。</summary>
    /// <remarks>
    /// ⚡ 高清模板匹配（2026-08-17 调整）：默认从 480 提到 <b>2400</b>。
    /// 用户模板均来自<b>手机原始截图</b>，若要模板与视频流 1:1 匹配、相似度接近 1.0，
    /// 视频流分辨率必须接近设备原始分辨率。设备长边通常约 2248（如 1080×2248），
    /// 故 MaxSize 设 2400（≥ 长边）即可保持原始分辨率传输（约 1080×2248）。
    /// <para>代价：软解 / 网络 / 设备编码开销显著上升（约为 480 时的 16–25× 像素量）。
    /// 多设备同屏时可能掉帧或卡顿；个人单 / 双设备使用无碍。
    /// 若需恢复性能，可调到 1080（视频流约 518×1080，模板缩放到约 0.48 倍仍可匹配，
    /// 只是相似度略降），或 480（原性能档）。</para>
    /// <para>注意：卡片预览区仅约 236px 宽，高分辨率视频流在卡片上显示会<b>更模糊</b>
    /// （降采样更多），但<b>模板匹配在原始解码帧上进行，识别准确度不受影响</b>。</para>
    /// </remarks>
    public int MaxSize { get; set; } = 2400;

    /// <summary>视频码率（bps）。</summary>
    /// <remarks>
    /// ⚡ 高清档（2026-08-17 随 <see cref="MaxSize"/>=2400 调整）：默认从 2Mbps 提到 8Mbps。
    /// 高分辨率（约 1080×2248）下，2Mbps 会出现明显块效应，
    /// 8Mbps 对 30fps H264 足够清晰，保证模板匹配所需的边缘 / 文字细节。
    /// 带宽或设备编码吃紧时可降到 4Mbps。
    /// </remarks>
    public int VideoBitRate { get; set; } = 8_000_000;

    /// <summary>最大帧率。</summary>
    public int MaxFps { get; set; } = 30;

    /// <summary>
    /// OCR 文字识别引擎偏好："auto"（默认，Tesseract）/ "tesseract" / "windows"（已弃用，回退 Tesseract）。
    /// <para>
    /// 2026-08-19：移除 Windows.Media.Ocr 路线（连同 WindowsMediaOcrTextRecognizer），
    /// 统一走 Tesseract，完全照搬 D:\新建文件夹\OcrViewer 的 OCR 机制（OcrEngine.Recognize：stdout 纯文本）。
    /// 中文游戏 UI 小字在 Tesseract（chi_sim+eng、放大 2 倍、灰度化）下识别率更高。
    /// </para>
    /// </summary>
    public string OcrEngine { get; set; } = "auto";

    /// <summary>Tesseract 可执行文件路径；为空则按默认安装目录、PATH、程序目录探测。</summary>
    public string TesseractPath { get; set; } = string.Empty;

    /// <summary>Tesseract 语言包，默认 chi_sim+eng。</summary>
    public string OcrLanguage { get; set; } = "chi_sim+eng";

    /// <summary>Tesseract 页面分割模式（PSM），默认 6（单一文本块）。</summary>
    public int OcrTesseractPsm { get; set; } = 6;

    /// <summary>Tesseract OCR 引擎模式（OEM），默认 1（LSTM）。</summary>
    public int OcrTesseractOem { get; set; } = 1;

    /// <summary>OCR 预处理放大倍数，默认 2.0；小于等于 1 视为不放大。</summary>
    public double OcrPreprocessScale { get; set; } = 2.0;

    /// <summary>OCR 预处理是否先转灰度，默认 true。</summary>
    public bool OcrGrayscale { get; set; } = true;

    /// <summary>设备扫描间隔（毫秒）。</summary>
    public int ScanIntervalMs { get; set; } = 2000;

    /// <summary>状态（电量）轮询间隔（毫秒）。</summary>
    public int StatusIntervalMs { get; set; } = 30000;

    /// <summary>同时投屏的设备上限（PRD v1.2 Q1 = 8）。</summary>
    public int MaxDevices { get; set; } = 8;

    /// <summary>截图保存目录，默认 <c>{我的图片}\MultiScrcpy</c>。</summary>
    public string ScreenshotDir { get; set; } = DefaultScreenshotDir();

    /// <summary>FFmpeg 原生库目录；为空则用 <c>{BaseDirectory}\ffmpeg\x64</c>。</summary>
    public string FFmpegPath { get; set; } = string.Empty;

    /// <summary>adb 单次命令超时（毫秒）。</summary>
    public int AdbTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// swscale 缩放算法标志（1 = SWS_FAST_BILINEAR，2 = SWS_BILINEAR，<b>4 = SWS_BICUBIC（默认）</b>）。
    /// <para>
    /// ⭐ 画面模糊修复：投屏是<b>降采样</b>场景（如 1080x2340 → 224x480），SWS_BILINEAR 只取 2x2
    /// 邻域，大比例缩小时高频细节直接丢失、观感发虚；SWS_BICUBIC 取 4x4 邻域并带锐化型基函数，
    /// 文字边缘明显更清晰，单帧开销在几百微秒量级，8 台同屏仍可接受。
    /// CPU 吃紧时可在 <c>config/settings.json</c> 里改回 2 或 1。
    /// </para>
    /// </summary>
    public int SwsFlags { get; set; } = 4;

    /// <summary>UI 卡片基准宽度（像素，100% 缩放时）。默认 240，配合 600 高度得到画面区 236×506（r≈0.466，贴合 9:19.3 长屏）。</summary>
    public int CardBaseWidth { get; set; } = 240;

    /// <summary>UI 卡片基准高度（像素，100% 缩放时）。默认 600，画面区高 = 高 − 94（标题 26 + 按键区 64 + 边框内边距 4）。</summary>
    public int CardBaseHeight { get; set; } = 600;

    /// <summary>默认截图目录。</summary>
    public static string DefaultScreenshotDir()
    {
        try
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = AppContext.BaseDirectory;
            return Path.Combine(pictures, "MultiScrcpy");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "screenshots");
        }
    }

    /// <summary>默认配置文件路径。</summary>
    public static string DefaultConfigPath() => Path.Combine(AppContext.BaseDirectory, "config", "settings.json");

    /// <summary>
    /// 从 JSON 载入配置；文件不存在或解析失败时返回默认配置（并记录警告）。
    /// </summary>
    public static AppConfig Load(string? path = null)
    {
        string file = string.IsNullOrWhiteSpace(path) ? DefaultConfigPath() : path!;
        try
        {
            if (!File.Exists(file))
            {
                Log.Info($"配置文件不存在，使用默认配置并生成：{file}");
                var fresh = new AppConfig();
                fresh.Save(file);
                return fresh;
            }

            string json = File.ReadAllText(file, Encoding.UTF8);
            AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg == null)
            {
                Log.Warn($"配置文件内容为空，使用默认配置：{file}");
                return new AppConfig();
            }

            cfg.Normalize();
            Log.Info($"已载入配置：{file}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error($"载入配置失败，改用默认配置：{file}", ex);
            return new AppConfig();
        }
    }

    /// <summary>写回 JSON（UTF-8 无 BOM）。失败只记日志，不抛出。</summary>
    public void Save(string? path = null)
    {
        string file = string.IsNullOrWhiteSpace(path) ? DefaultConfigPath() : path!;
        try
        {
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(file, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log.Info($"已保存配置：{file}");
        }
        catch (Exception ex)
        {
            Log.Error($"保存配置失败：{file}", ex);
        }
    }

    /// <summary>把越界/空值收敛到合法范围。</summary>
    public void Normalize()
    {
        if (MaxDevices <= 0) MaxDevices = 8;
        if (MaxDevices > 32) MaxDevices = 32;
        if (PortBase < 1024 || PortBase > 60000) PortBase = 27183;
        if (MaxSize <= 0) MaxSize = 2400;
        if (VideoBitRate <= 0) VideoBitRate = 8_000_000;
        if (MaxFps <= 0 || MaxFps > 120) MaxFps = 30;
        if (ScanIntervalMs < 500) ScanIntervalMs = 500;
        if (StatusIntervalMs < 2000) StatusIntervalMs = 2000;
        if (AdbTimeoutMs < 1000) AdbTimeoutMs = 1000;
        if (string.IsNullOrWhiteSpace(ServerVersion)) ServerVersion = "4.0";
        if (string.IsNullOrWhiteSpace(VideoCodec)) VideoCodec = "h264";
        if (string.IsNullOrWhiteSpace(ScreenshotDir)) ScreenshotDir = DefaultScreenshotDir();
        if (SwsFlags <= 0) SwsFlags = 2;
        if (CardBaseWidth < 160) CardBaseWidth = 240;
        if (CardBaseHeight < 240) CardBaseHeight = 600;
    }

    /// <summary>
    /// 解析可用的 adb 可执行文件路径：先看 <see cref="AdbPath"/>，再遍历 PATH。
    /// </summary>
    /// <exception cref="AdbException">两处都找不到。</exception>
    public string ResolveAdb()
    {
        if (!string.IsNullOrWhiteSpace(AdbPath))
        {
            if (File.Exists(AdbPath)) return Path.GetFullPath(AdbPath);
            throw new AdbException($"配置中指定的 adb 不存在：{AdbPath}");
        }

        string? found = FindInPath("adb.exe") ?? FindInPath("adb");
        if (found != null) return found;

        // 额外识别程序自身目录下的 adb（便于免 PATH 部署）：
        // 支持平铺 adb.exe，以及官方 platform-tools 目录布局 platform-tools\adb.exe
        string[] localCandidates =
        {
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
        };
        foreach (string cand in localCandidates)
        {
            if (File.Exists(cand)) return Path.GetFullPath(cand);
        }

        throw new AdbException(
            "未找到 adb。请安装 Android Platform-Tools 并加入 PATH，" +
            "或将 platform-tools 目录（含 adb.exe）放到程序目录下，" +
            "或在 config/settings.json 的 AdbPath 中显式指定 adb.exe 的完整路径。");
    }

    /// <summary>解析 scrcpy-server jar 的实际路径（不存在也原样返回，由启动器给出明确报错）。</summary>
    public string ResolveServerJar()
    {
        if (!string.IsNullOrWhiteSpace(ServerJarPath)) return Path.GetFullPath(ServerJarPath);
        return Path.Combine(AppContext.BaseDirectory, "assets", $"scrcpy-server-v{ServerVersion}.jar");
    }

    /// <summary>解析 FFmpeg 原生库目录。</summary>
    public string ResolveFFmpegDir()
    {
        if (!string.IsNullOrWhiteSpace(FFmpegPath)) return Path.GetFullPath(FFmpegPath);
        return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "x64");
    }

    private static string? FindInPath(string fileName)
    {
        try
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;

            foreach (string raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // PATH 中可能有非法路径项，跳过
                }
            }
        }
        catch
        {
            // 读取环境变量失败时视为未找到
        }

        return null;
    }
}
