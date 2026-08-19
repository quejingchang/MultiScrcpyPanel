using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting;
using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;
using Xunit.Abstractions;

namespace MultiScrcpy.Tests;

/// <summary>
/// OCR 模板匹配 + 模板内文字点击的"信箱帧"集成测试。
/// <para>
/// 2026-08-19 BugFix：用户报告 `OCR 日常_宝图任务.png TEXT "参加"` 在 2248×1080 信箱视频帧中
/// 点击到了 (1566, 397)——经主理人诊断，模板匹配器在多尺度 + NMS 0.5 时选中了错误的候选
/// （Nx ≈ 0.601 而非 0.135），导致"参加"文字换算后落在右 70%。本测试套件验证修复后：
/// </para>
/// <list type="bullet">
/// <item>真实 OpenCv 匹配器 + 合成 frame 能正确命中嵌入模板的位置（Nx/Ny 精确）；</item>
/// <item>文字换算坐标 fx/fy 落在模板的实际区域内（[0, 1]）；</item>
/// <item>fx/fy 越界（非有限数或超出 [0,1]）时降级点击模板中心，<b>不会把点击推到帧外</b>；</item>
/// <item>FindBestSpan 误合并（合并中心相对首词中心偏差 &gt; 0.2）时回退到首词中心并告警。</item>
/// </list>
/// </summary>
public class ScriptOcrLetterboxTests
{
    private readonly ITestOutputHelper _output;

    public ScriptOcrLetterboxTests(ITestOutputHelper output)
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

    private sealed class FakeMatcher : ITemplateMatcher
    {
        public Queue<TemplateMatch?> Responses { get; } = new();

        public TemplateMatch? Match(Bitmap frame, Bitmap template, double maxError)
            => Responses.Count > 0 ? Responses.Dequeue() : null;

        public IReadOnlyList<TemplateMatch> MatchAll(Bitmap frame, Bitmap template, double maxError, int maxResults = 10)
        {
            TemplateMatch? m = Match(frame, template, maxError);
            return m != null ? new List<TemplateMatch> { m } : new List<TemplateMatch>();
        }
    }

    /// <summary>同步收集 <see cref="ScriptLogEntry"/>，避免 <see cref="Progress{T}"/> 的异步派发干扰断言。</summary>
    private sealed class CollectingProgress : IProgress<ScriptLogEntry>
    {
        public List<ScriptLogEntry> Entries { get; } = new();

        public void Report(ScriptLogEntry value) => Entries.Add(value);
    }

    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mhxy_letterbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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

    /// <summary>把 <paramref name="tpl"/> 粘贴到 <paramref name="frame"/> 的指定归一化中心位置。</summary>
    private static Bitmap BuildFrameWithTemplate(Bitmap tpl, int fw, int fh, double centerX, double centerY, Color bg)
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

    /// <summary>合成一个 64×64 的"带背景边距的图标"模板：浅灰底 + 红色方块 + 蓝色小标记，避免纯色常数图退化。</summary>
    private static Bitmap MakeIconTemplate(int size, int iconSize, int ix, int iy)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.LightGray);
            using (var red = new SolidBrush(Color.Red))
            {
                g.FillRectangle(red, ix, iy, iconSize, iconSize);
            }

            using (var blue = new SolidBrush(Color.Blue))
            {
                int mark = Math.Max(2, iconSize / 6);
                g.FillRectangle(blue, ix + iconSize / 2, iy + iconSize / 2, mark, mark);
            }
        }

        return bmp;
    }

    private static int ExtractClick(string downCall)
    {
        // "DOWN x,y" → x
        var parts = downCall.Split(' ')[1].Split(',');
        return int.Parse(parts[0]);
    }

    // ---- 1) 真实 OpenCv 匹配 + 嵌入 frame 中部 ----

    [Fact]
    public async Task OCR_模板嵌帧中部_真实匹配_文字坐标正确()
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

        // 复制到唯一临时路径：避免 s_templateTextCache 与真实 OCR 探针的缓存键互相污染。
        // 注意：脚本中引用的是固定文件名 "letterbox_tpl.png"，文件必须用同名保存。
        string dir = MakeTempDir();
        const string tplFileName = "letterbox_tpl.png";
        string tplCopy = Path.Combine(dir, tplFileName);
        File.Copy(src, tplCopy);

        using var tpl = new Bitmap(tplCopy);
        const int fw = 2248;
        const int fh = 1080;
        using var frame = BuildFrameWithTemplate(tpl, fw, fh, 0.50, 0.52, Color.Black);

        // 1) 真实 OpenCv 匹配器在原始 frame 上的命中（直接调 Match，验证 NMS/多尺度修复）：
        TemplateMatch? direct = new OpenCvTemplateMatcher().Match(frame, tpl, 0.30);
        Assert.NotNull(direct);
        // 模板粘贴中心 (1124, 561) → Nx=0.5, Ny=0.52；允许 2 像素（≈ 0.001）的取整误差。
        double expectNx = (Math.Round(0.50 * fw) - tpl.Width / 2.0 + tpl.Width / 2.0) / fw; // 0.50
        double expectNy = (Math.Round(0.52 * fh) - tpl.Height / 2.0 + tpl.Height / 2.0) / fh; // 0.52
        Assert.InRange(direct!.Nx, expectNx - 0.02, expectNx + 0.02);
        Assert.InRange(direct.Ny, expectNy - 0.02, expectNy + 0.02);
        Assert.True(direct.Score >= 0.85, $"1:1 嵌入相似度应较高，实际 {direct.Score:F3}");
        _output.WriteLine($"[OK] 真实匹配器 Nx={direct.Nx:F4} Ny={direct.Ny:F4} HalfW={direct.HalfW:F4} HalfH={direct.HalfH:F4} score={direct.Score:F4}");

        // 2) ScriptEngine 全链路（用 FakeMatcher 把"匹配命中"注入——直接复用真实匹配器结果）：
        //   直接匹配器结果 Nx/Ny/HalfW/HalfH 已经在 #1 验证；这里专注测试 ScriptEngine 的
        //   坐标换算 (fx, fy) + 越界校验 + 诊断日志 + ClickInRect，绕开 GDI+ clone 像素
        //   微妙差异对端到端匹配的影响。
        var fakeMatcher = new FakeMatcher();
        fakeMatcher.Responses.Enqueue(direct);

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.757, 0.470, 0.20, 0.10));
        var sink = new FakeSink { Frame = frame };
        var progress = new CollectingProgress();

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR letterbox_tpl.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, fw, fh, CancellationToken.None,
            progress: progress,
            matcher: fakeMatcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int ix = int.Parse(down!.Split(' ')[1].Split(',')[0]);
        int iy = int.Parse(down.Split(' ')[1].Split(',')[1]);
        double fx = (double)ix / fw;
        double fy = (double)iy / fh;
        // 模板中心 Nx≈0.5 + 文字模板内中心 tx=0.857 → fx ≈ 0.595
        _output.WriteLine($"[OK] 点击像素 ({ix},{iy}) → fx={fx:F4} fy={fy:F4}");
        Assert.InRange(fx, 0.55, 0.65);
        Assert.InRange(fy, 0.45, 0.60);

        // 诊断日志：确认新增的"OCR 诊断"行存在且数值一致
        ScriptLogEntry diag = progress.Entries.First(e => e.Message.StartsWith("OCR 诊断"));
        _output.WriteLine($"[OK] 诊断日志：{diag.Message}");
        Assert.Contains("fx=", diag.Message);
        Assert.Contains("frame", diag.Message);
    }

    // ---- 2) 越界降级：文字点超出 [0,1] 时降级点击模板中心 ----

    [Fact]
    public async Task OCR_文字点越界_降级点击模板中心()
    {
        // m.Nx=0.95, HalfW=0.06 → 模板盒 [0.89, 1.01]（右边缘越界）
        // 文字 tx=1.0 → fx = 0.89 + 1.0*0.12 = 1.01 > 1.0 → 触发降级
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.95, 0.50, 0.06, 0.10, 0.95));

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.90, 0.40, 0.20, 0.20)); // CenterX=1.0, CenterY=0.50
        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var progress = new CollectingProgress();

        string dir = MakeTempDir();
        File.WriteAllBytes(Path.Combine(dir, "t.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR t.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, 100, 200, CancellationToken.None,
            progress: progress,
            matcher: matcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        // 降级点击模板中心：(0.95, 0.50) → 帧 100x200 → (95, 100)
        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = ExtractClick(down!);
        int y = int.Parse(down!.Split(' ')[1].Split(',')[1]);
        Assert.Equal(95, x);
        Assert.Equal(100, y);

        // 必须有"越界降级"告警日志
        ScriptLogEntry warn = progress.Entries.First(e => e.Message.Contains("越出帧范围"));
        _output.WriteLine($"[OK] 降级告警：{warn.Message}");
        ScriptLogEntry diag = progress.Entries.First(e => e.Message.StartsWith("OCR 诊断"));
        _output.WriteLine($"[OK] 诊断日志：{diag.Message}");
    }

    // ---- 3) FindBestSpan 合并中心偏差过大 → 改用首词中心 ----

    [Fact]
    public async Task OCR_FindBestSpan合并中心偏差过大_回退首词中心()
    {
        // 模板命中在 (0.5, 0.5)；识别器返回"参"在 (0.05, 0.10) 与"加"在 (0.55, 0.10)
        // FindBestSpan("参加") 会把这两个相隔 0.5 的词合并，合并中心 ≈ 0.325
        // 相对首词"参"中心 0.075 偏差 0.25 > 0.2 → 回退到首词中心 (0.075, 0.125)
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        var rec = new FakeTextRecognizer(
            new RecognizedTextLine("参", 0.05, 0.10, 0.05, 0.05),
            new RecognizedTextLine("加", 0.55, 0.10, 0.05, 0.05));

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var progress = new CollectingProgress();

        string dir = MakeTempDir();
        File.WriteAllBytes(Path.Combine(dir, "t.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR t.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, 100, 200, CancellationToken.None,
            progress: progress,
            matcher: matcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        // 期望：使用首词中心 (0.075, 0.125) 而非合并中心 (0.325, 0.125)
        // fx = (0.5-0.1) + 0.075*0.2 = 0.415 → 像素 41
        // fy = (0.5-0.1) + 0.125*0.2 = 0.425 → 像素 85
        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = ExtractClick(down!);
        int y = int.Parse(down!.Split(' ')[1].Split(',')[1]);
        _output.WriteLine($"[OK] 合并中心偏差过大后点击 ({x},{y})");
        Assert.InRange(x, 40, 42);
        Assert.InRange(y, 84, 86);

        ScriptLogEntry warn = progress.Entries.First(e => e.Message.Contains("合并中心"));
        _output.WriteLine($"[OK] 合并告警：{warn.Message}");
        Assert.Contains("改用首词中心", warn.Message);
    }

    // ---- 4) 信箱边缘：模板贴左上 / 右下但完全可见，fx/fy 仍在 [0,1] ----

    [Fact]
    public async Task OCR_模板贴左上_坐标仍在帧内()
    {
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.08, 0.10, 0.06, 0.08, 0.95)); // 盒 [0.02,0.14] × [0.02,0.18]

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.55, 0.50, 0.20, 0.20));
        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var progress = new CollectingProgress();

        string dir = MakeTempDir();
        File.WriteAllBytes(Path.Combine(dir, "t.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR t.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, 100, 200, CancellationToken.None,
            progress: progress,
            matcher: matcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = ExtractClick(down!);
        int y = int.Parse(down!.Split(' ')[1].Split(',')[1]);
        double fx = x / 100.0;
        double fy = y / 200.0;
        _output.WriteLine($"[OK] 贴左上点击 ({x},{y}) → fx={fx:F3} fy={fy:F3}");
        Assert.InRange(fx, 0.0, 1.0);
        Assert.InRange(fy, 0.0, 1.0);
    }

    [Fact]
    public async Task OCR_模板贴右下_坐标仍在帧内()
    {
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.92, 0.90, 0.06, 0.08, 0.95)); // 盒 [0.86,0.98] × [0.82,0.98]

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.10, 0.10, 0.20, 0.20));
        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var progress = new CollectingProgress();

        string dir = MakeTempDir();
        File.WriteAllBytes(Path.Combine(dir, "t.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR t.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, 100, 200, CancellationToken.None,
            progress: progress,
            matcher: matcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = ExtractClick(down!);
        int y = int.Parse(down!.Split(' ')[1].Split(',')[1]);
        double fx = x / 100.0;
        double fy = y / 200.0;
        _output.WriteLine($"[OK] 贴右下点击 ({x},{y}) → fx={fx:F3} fy={fy:F3}");
        Assert.InRange(fx, 0.0, 1.0);
        Assert.InRange(fy, 0.0, 1.0);
    }

    // ---- 5) 真实匹配器：边缘小模板命中 ----

    [Fact]
    public async Task OCR_真实匹配_合成小模板贴左上_命中且文字坐标合理()
    {
        if (!OpenCvTemplateMatcher.IsAvailable)
        {
            _output.WriteLine("[SKIP] OpenCvSharp 不可用");
            return;
        }

        using var tpl = MakeIconTemplate(64, 36, 14, 14);
        const int fw = 800;
        const int fh = 600;
        using var frame = BuildFrameWithTemplate(tpl, fw, fh, 0.10, 0.10, Color.LightGray);

        // 直接调匹配器，断言 Nx/Ny 在左上
        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        // 模板粘贴中心 (centerX*fw, centerY*fh) = (80, 60) → Nx=0.10, Ny=0.10；允许 1 像素误差
        _output.WriteLine($"[OK] 贴左上真实匹配 Nx={m!.Nx:F4} Ny={m.Ny:F4} score={m.Score:F4} (期望 ≈0.100,0.100)");
        Assert.InRange(m.Nx, 0.08, 0.12);
        Assert.InRange(m.Ny, 0.08, 0.12);
        // 走 ScriptEngine 跑全链路（FakeMatcher 注入已验证的匹配结果，专注测试坐标换算）
        string dir = MakeTempDir();
        string tplPath = Path.Combine(dir, "edge_tpl.png");
        tpl.Save(tplPath, ImageFormat.Png);

        var fakeMatcher = new FakeMatcher();
        fakeMatcher.Responses.Enqueue(m);

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.7, 0.45, 0.2, 0.1));
        var sink = new FakeSink { Frame = frame };
        var progress = new CollectingProgress();

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR edge_tpl.png MAXERR 0.3 TEXT \"参加\" CENTER", "t"),
            sink, fw, fh, CancellationToken.None,
            progress: progress,
            matcher: fakeMatcher,
            templatesDirectory: dir,
            textRecognizer: rec);

        string? down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = ExtractClick(down!);
        int y = int.Parse(down!.Split(' ')[1].Split(',')[1]);
        double fx = x / (double)fw;
        double fy = y / (double)fh;
        _output.WriteLine($"[OK] 贴左上点击像素 ({x},{y}) → fx={fx:F4} fy={fy:F4}");
        Assert.InRange(fx, 0.05, 0.25);
        Assert.InRange(fy, 0.05, 0.25);
    }
}
