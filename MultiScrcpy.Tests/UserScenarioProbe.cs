using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting;
using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;
using Xunit.Abstractions;

namespace MultiScrcpy.Tests;

/// <summary>
/// 用户场景一次性探针（QA）：复现 `mhxyOCR_宝图任务.scr` 的 `OCR 日常_宝图任务.png TEXT "参加"`
/// 在 2248×1080 信箱视频帧中点击错位 (1566, 397) 的 bug。
/// <para>
/// 2026-08-19 BugFix 验证：构造合成帧（把真实模板 `日常_宝图任务.png` 嵌入到已知位置），
/// 跑真实 <see cref="OpenCvTemplateMatcher"/> + 真实 <see cref="TesseractTextRecognizer"/>
/// 三通道融合，捕获 <see cref="ScriptEngine"/> 的诊断日志与 <see cref="OpenCvTemplateMatcher"/>
/// 的候选数诊断（通过自定义 <see cref="TraceListener"/>），输出全链路坐标。
/// </para>
/// <para>
/// 探针运行时间约 5-15 秒（依赖 Tesseract），用 <see cref="ITestOutputHelper"/> 输出。
/// 缺 Tesseract/OpenCvSharp 模板时优雅跳过。
/// </para>
/// </summary>
public class UserScenarioProbe
{
    private readonly ITestOutputHelper _output;

    public UserScenarioProbe(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class FakeSink : IScriptDeviceSink
    {
        public List<string> Calls { get; } = new();
        public Bitmap? Frame { get; set; }

        public Bitmap? GetCurrentFrame()
        {
            if (Frame == null)
            {
                return null;
            }

            return Frame.Clone(new Rectangle(0, 0, Frame.Width, Frame.Height), Frame.PixelFormat);
        }

        public void TouchDown(int x, int y) => Calls.Add($"DOWN {x},{y}");
        public void TouchMove(int x, int y) => Calls.Add($"MOVE {x},{y}");
        public void TouchUp(int x, int y) => Calls.Add($"UP {x},{y}");
        public void KeyPress(int c) => Calls.Add($"KEYP {c}");
        public void KeyDown(int c) => Calls.Add($"KEYD {c}");
        public void KeyUp(int c) => Calls.Add($"KEYU {c}");
        public void SendText(string t) => Calls.Add("TEXT " + t);
    }

    private sealed class CollectingProgress : IProgress<ScriptLogEntry>
    {
        public List<ScriptLogEntry> Entries { get; } = new();

        public void Report(ScriptLogEntry value) => Entries.Add(value);
    }

    /// <summary>把 <see cref="Debug.WriteLine"/> 输出捕获到 <see cref="ITestOutputHelper"/>。</summary>
    private sealed class DebugTraceListener : TraceListener
    {
        private readonly ITestOutputHelper _output;

        public DebugTraceListener(ITestOutputHelper output)
        {
            _output = output;
            Name = "UserScenarioProbeListener";
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _output.WriteLine($"[Debug] {message}");
            }
        }
    }

    private static string? FindTemplate(string name)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\templates", name),
            Path.Combine(Directory.GetCurrentDirectory(), "templates", name),
        };
        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static Bitmap BuildLetterboxFrame(Bitmap tpl, int fw, int fh, double centerX, double centerY, Color bg)
    {
        var frame = new Bitmap(fw, fh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(bg);
            int x = (int)Math.Round(centerX * fw - tpl.Width / 2.0);
            int y = (int)Math.Round(centerY * fh - tpl.Height / 2.0);
            g.DrawImage(tpl, x, y, tpl.Width, tpl.Height);
        }

        return frame;
    }

    /// <summary>解析 ScriptEngine 新增的"OCR 诊断"日志行，抽取 (Nx, Ny, HalfW, HalfH, tx, ty, fx, fy)。</summary>
    private static (double Nx, double Ny, double HalfW, double HalfH, double Tx, double Ty, double Fx, double Fy, int FrameW, int FrameH)?
        ParseDiagLog(string message)
    {
        // 格式: OCR 诊断 模板0.134x0.075@0.499,0.520 文字0.857,0.520→fx=0.595,0.545 frame2248x1080
        var match = Regex.Match(
            message,
            @"模板(?<hw>\d+\.\d+)x(?<hh>\d+\.\d+)@(?<nx>\d+\.\d+),(?<ny>\d+\.\d+)\s+文字(?<tx>\d+\.\d+),(?<ty>\d+\.\d+)→fx=(?<fx>\d+\.\d+),(?<fy>\d+\.\d+)\s+frame(?<fw>\d+)x(?<fh>\d+)");
        if (!match.Success)
        {
            return null;
        }

        return (
            double.Parse(match.Groups["nx"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["ny"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["hw"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["hh"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["tx"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["ty"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["fx"].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups["fy"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["fw"].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups["fh"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task 用户场景_模板嵌帧中部_真实OCR全链路_诊断输出()
    {
        if (!OpenCvTemplateMatcher.IsAvailable)
        {
            _output.WriteLine("[SKIP] OpenCvSharp 不可用");
            return;
        }

        string? src = FindTemplate("日常_宝图任务.png");
        if (src == null)
        {
            _output.WriteLine("[SKIP] 未找到 templates/日常_宝图任务.png");
            return;
        }

        // 用唯一临时路径的模板副本（避免 s_templateTextCache 与其他探针互相污染）
        string dir = Path.Combine(Path.GetTempPath(), "mhxy_probe_center_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string tplPath = Path.Combine(dir, "日常_宝图任务.png");
        File.Copy(src, tplPath);

        // 把"日常_宝图任务.png"复制到帧中部（中心 Nx=0.5, Ny=0.52）
        using var tpl = new Bitmap(tplPath);
        const int fw = 2248;
        const int fh = 1080;
        using var frame = BuildLetterboxFrame(tpl, fw, fh, 0.50, 0.52, Color.Black);

        // 真实 Tesseract 识别器
        var rec = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!rec.IsAvailable)
        {
            _output.WriteLine("[SKIP] Tesseract 不可用（未找到 tesseract.exe 或语言包）");
            return;
        }

        var sink = new FakeSink { Frame = frame };
        var progress = new CollectingProgress();

        // 注入 Debug 监听器，捕获 [TemplateMatcher] 候选数诊断
        var listener = new DebugTraceListener(_output);
        Trace.Listeners.Add(listener);
        try
        {
            await ScriptEngine.ExecuteAsync(
                ScriptEngine.Parse("OCR 日常_宝图任务.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
                sink, fw, fh, CancellationToken.None,
                progress: progress,
                matcher: new OpenCvTemplateMatcher(),
                templatesDirectory: dir,
                textRecognizer: rec);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        // 抓点击像素（如果有）
        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        _output.WriteLine("");
        _output.WriteLine("===== 用户场景探针 #1：模板嵌帧中部 (2248×1080) =====");
        _output.WriteLine($"点击像素: {down ?? "（无 DOWN 调用）"}");

        // 抓诊断日志（record struct 的 default Message 为 null，用此判定未命中）
        ScriptLogEntry diag = progress.Entries.FirstOrDefault(e => e.Message.StartsWith("OCR 诊断"));
        if (diag.Message == null)
        {
            _output.WriteLine("⚠️ 未找到 OCR 诊断日志（模板可能未命中或文字未找到）");
        }
        else
        {
            _output.WriteLine($"诊断日志: {diag.Message}");
            var parsed = ParseDiagLog(diag.Message);
            if (parsed.HasValue)
            {
                var (Nx, Ny, HalfW, HalfH, Tx, Ty, Fx, Fy, frameW, frameH) = parsed.Value;
                _output.WriteLine("");
                _output.WriteLine($"  Nx     = {Nx:F4}");
                _output.WriteLine($"  Ny     = {Ny:F4}");
                _output.WriteLine($"  HalfW  = {HalfW:F4}");
                _output.WriteLine($"  HalfH  = {HalfH:F4}");
                _output.WriteLine($"  tx     = {Tx:F4}");
                _output.WriteLine($"  ty     = {Ty:F4}");
                _output.WriteLine($"  fx     = {Fx:F4}");
                _output.WriteLine($"  fy     = {Fy:F4}");
                _output.WriteLine($"  frame  = {frameW}x{frameH}");

                // 主断言：模板匹配位置精确（嵌中部）
                Assert.InRange(Nx, 0.45, 0.55);
                Assert.InRange(Ny, 0.50, 0.55);

                // 文字 fx 落在模板内 (中心 0.5 + 文字在模板内 0.857 → 期望 0.595)
                Assert.InRange(Fx, 0.55, 0.65);
                Assert.InRange(Fy, 0.45, 0.60);
            }
        }

        // 打印所有相关日志（按行）
        _output.WriteLine("");
        _output.WriteLine("===== 全部 OCR 相关日志 =====");
        foreach (ScriptLogEntry e in progress.Entries.Where(x =>
                     x.Message.Contains("OCR") || x.Message.Contains("命中") || x.Message.Contains("命中点") || x.Message.Contains("文字")))
        {
            _output.WriteLine($"  {e.Message}");
        }
    }

    [Fact]
    public async Task 用户场景_信箱frame_手机画面左置_不误命中()
    {
        if (!OpenCvTemplateMatcher.IsAvailable)
        {
            _output.WriteLine("[SKIP] OpenCvSharp 不可用");
            return;
        }

        string? src = FindTemplate("日常_宝图任务.png");
        if (src == null)
        {
            _output.WriteLine("[SKIP] 未找到 templates/日常_宝图任务.png");
            return;
        }

        // 真实场景：手机画面占帧左 ~27%，模板嵌在手机画面水平中心（Nx ≈ 0.135）
        // 这是 2026-08-19 真实用户 bug 场景的合成复现
        string dir = Path.Combine(Path.GetTempPath(), "mhxy_probe_letterbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string tplPath = Path.Combine(dir, "日常_宝图任务.png");
        File.Copy(src, tplPath);

        using var tpl = new Bitmap(tplPath);
        const int fw = 2248;
        const int fh = 1080;
        // 模板在手机画面水平中心：手机画面占 0..0.27，中心 0.135
        using var frame = BuildLetterboxFrame(tpl, fw, fh, 0.135, 0.52, Color.Black);

        var rec = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!rec.IsAvailable)
        {
            _output.WriteLine("[SKIP] Tesseract 不可用");
            return;
        }

        var sink = new FakeSink { Frame = frame };
        var progress = new CollectingProgress();
        var listener = new DebugTraceListener(_output);
        Trace.Listeners.Add(listener);
        try
        {
            await ScriptEngine.ExecuteAsync(
                ScriptEngine.Parse("OCR 日常_宝图任务.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
                sink, fw, fh, CancellationToken.None,
                progress: progress,
                matcher: new OpenCvTemplateMatcher(),
                templatesDirectory: dir,
                textRecognizer: rec);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        _output.WriteLine("");
        _output.WriteLine("===== 用户场景探针 #2：信箱 frame 手机画面左置 (2248×1080, Nx=0.135) =====");
        _output.WriteLine($"点击像素: {down ?? "（无 DOWN 调用）"}");

        ScriptLogEntry diag = progress.Entries.FirstOrDefault(e => e.Message.StartsWith("OCR 诊断"));
        if (diag.Message == null)
        {
            _output.WriteLine("⚠️ 未找到 OCR 诊断日志");
            return;
        }

        _output.WriteLine($"诊断日志: {diag.Message}");
        var parsed = ParseDiagLog(diag.Message);
        if (!parsed.HasValue)
        {
            return;
        }

        var (Nx, Ny, HalfW, HalfH, Tx, Ty, Fx, Fy, frameW, frameH) = parsed.Value;
        _output.WriteLine("");
        _output.WriteLine($"  Nx     = {Nx:F4}（期望 ≈ 0.135）");
        _output.WriteLine($"  Ny     = {Ny:F4}（期望 ≈ 0.52）");
        _output.WriteLine($"  HalfW  = {HalfW:F4}");
        _output.WriteLine($"  HalfH  = {HalfH:F4}");
        _output.WriteLine($"  tx     = {Tx:F4}");
        _output.WriteLine($"  ty     = {Ty:F4}");
        _output.WriteLine($"  fx     = {Fx:F4}（期望 ≈ 0.23）");
        _output.WriteLine($"  fy     = {Fy:F4}（期望 ≈ 0.52）");

        _output.WriteLine("");
        _output.WriteLine("===== 全部 OCR 相关日志 =====");
        foreach (ScriptLogEntry e in progress.Entries.Where(x =>
                     x.Message.Contains("OCR") || x.Message.Contains("命中") || x.Message.Contains("命中点") || x.Message.Contains("文字")))
        {
            _output.WriteLine($"  {e.Message}");
        }

        // 核心断言：模板匹配位置应落在手机画面内（Nx ∈ [0.10, 0.17]），不能漂到 0.6+
        Assert.InRange(Nx, 0.10, 0.17);
        Assert.InRange(Ny, 0.50, 0.55);

        // 文字 fx 应在手机画面内（≈ 0.18..0.30），绝对不能 ≥ 0.45（旧 bug 落在 0.697）
        Assert.InRange(Fx, 0.18, 0.30);
        Assert.InRange(Fy, 0.45, 0.60);

        // 强化断言：旧 bug 的 fx=0.697 必须不再出现
        Assert.True(Fx < 0.45, $"fx={Fx} 已漂到 ≥ 0.45（与 2026-08-19 bug 行为一致），修复失败");
    }
}
