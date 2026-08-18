using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Decoder;
using MultiScrcpy.UI;

namespace MultiScrcpy;

/// <summary>
/// 应用入口（架构文档 §8 T05-6）。
/// <para>
/// 启动顺序 <b>不可调换</b>：
/// <list type="number">
///   <item><description><see cref="ApplicationConfiguration.Initialize"/>：读 csproj 的 HighDpiMode / VisualStyles，
///   必须早于任何窗口创建，否则 PerMonitorV2 不生效。</description></item>
///   <item><description><see cref="Log.Setup"/>：让后续所有失败都有落盘记录。</description></item>
///   <item><description><see cref="AppConfig.Load"/>：拿到 FFmpeg 路径等配置。</description></item>
///   <item><description><see cref="FFmpegBinariesHelper.Register"/>：<b>必须在任何 <c>ffmpeg.*</c> 调用之前</b>，
///   失败即致命，用 MessageBox 展示修复指引后退出（全项目唯一允许的模态弹窗场景，见 §6.1）。</description></item>
///   <item><description>挂接全局异常兜底，再 <c>Application.Run</c>。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>配置文件相对路径（相对于程序输出目录）。</summary>
    private const string ConfigRelativePath = "config/settings.json";

    [STAThread]
    private static void Main()
    {
        // 1) WinForms 全局配置（HighDpiMode / VisualStyles / TextRendering）
        ApplicationConfiguration.Initialize();

        // 2) 日志：越早越好，后面每一步失败都要有落盘记录
        Log.Setup();
        LogEnvironment();

        // 3) 配置
        AppConfig cfg = LoadConfig();

        // 4) FFmpeg 原生库注册（致命失败点）
        string ffmpegVersion;
        try
        {
            FFmpegBinariesHelper.Register(cfg.FFmpegPath);
            ffmpegVersion = FFmpegBinariesHelper.VersionInfo();
            Log.Info($"FFmpeg 版本：{ffmpegVersion}");
        }
        catch (DecoderException ex)
        {
            Log.Error("FFmpeg 初始化失败，程序退出。", ex);
            MessageBox.Show(ex.Message, "FFmpeg 初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;   // 唯一允许的模态弹窗场景
        }
        catch (Exception ex)
        {
            // 兜底：任何非预期异常也要给出可读提示，而不是静默崩在启动屏之前
            Log.Error("FFmpeg 初始化时发生未预期异常，程序退出。", ex);
            MessageBox.Show($"初始化视频解码环境时发生未预期错误：{Environment.NewLine}{ex.Message}",
                            "FFmpeg 初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 5) 全局异常兜底：绝不让后台线程或 UI 线程的异常无声吞掉
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnUiThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskSchedulerUnobservedGuard.Install();

        // 6) 主窗口
        try
        {
            Application.Run(new MainForm(cfg, ffmpegVersion));
        }
        catch (Exception ex)
        {
            Log.Error("主窗口运行期发生致命异常。", ex);
            throw;
        }
        finally
        {
            Application.ThreadException -= OnUiThreadException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            Log.Info("==== MultiScrcpyPanel 正常退出 ====");
        }
    }

    /// <summary>载入配置；任何异常都退化为默认配置，绝不阻断启动。</summary>
    private static AppConfig LoadConfig()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
            AppConfig cfg = AppConfig.Load(path);
            cfg.Normalize();
            Log.Info($"配置已就绪：设备上限 {cfg.MaxDevices}、编码 {cfg.VideoCodec}、" +
                     $"max_size {cfg.MaxSize}、码率 {cfg.VideoBitRate}、帧率 {cfg.MaxFps}");
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error("载入配置失败，改用默认配置。", ex);
            var fallback = new AppConfig();
            fallback.Normalize();
            return fallback;
        }
    }

    /// <summary>记录运行环境，便于用户反馈问题时一眼定位。</summary>
    private static void LogEnvironment()
    {
        try
        {
            Log.Info($"运行环境：{Environment.OSVersion} / .NET {Environment.Version} / " +
                     $"{(Environment.Is64BitProcess ? "x64" : "x86")} / " +
                     $"区域 {CultureInfo.CurrentCulture.Name}");
            Log.Info($"程序目录：{AppContext.BaseDirectory}");
            Log.Info($"日志文件：{Log.LogFilePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"记录运行环境失败：{ex.Message}");
        }
    }

    private static void OnUiThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        // UI 线程异常只记录，不弹窗：避免异常风暴时弹窗刷屏导致用户无法操作
        Log.Error("UI 线程未处理异常", e.Exception);
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Log.Error($"未处理异常（IsTerminating={e.IsTerminating}）", e.ExceptionObject as Exception);
    }
}

/// <summary>
/// 兜住被遗弃的 <c>Task</c> 异常。
/// <para>
/// <see cref="DeviceManager"/> 里的 <c>Task.Run</c>（扫描 / 轮询 / 重试授权）都是即发即忘，
/// 内部虽已 try/catch，但这里再兜一层，保证任何漏网异常都能落盘而不是静默丢失。
/// </para>
/// </summary>
internal static class TaskSchedulerUnobservedGuard
{
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("后台任务未观察异常", e.Exception);
            e.SetObserved();   // 阻止在 GC 终结器线程上重新抛出
        };
    }
}
