using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting;
using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>OCR 指令（单模板匹配 + 可选模板内文字定位点击）单元测试。</summary>
public class ScriptOcrTests
{
    /// <summary>记录控制动作 + 取帧次数的假 sink。</summary>
    private sealed class FakeSink : IScriptDeviceSink
    {
        public List<string> Calls { get; } = new();
        public int FrameFetches { get; private set; }
        public Bitmap? Frame { get; set; }

        public Bitmap? GetCurrentFrame()
        {
            FrameFetches++;
            return Frame?.Clone(new Rectangle(0, 0, Frame.Width, Frame.Height), Frame.PixelFormat);
        }

        public void TouchDown(int x, int y) => Calls.Add($"DOWN {x},{y}");
        public void TouchMove(int x, int y) => Calls.Add($"MOVE {x},{y}");
        public void TouchUp(int x, int y) => Calls.Add($"UP {x},{y}");
        public void KeyPress(int c) => Calls.Add($"KEYP {c}");
        public void KeyDown(int c) => Calls.Add($"KEYD {c}");
        public void KeyUp(int c) => Calls.Add($"KEYU {c}");
        public void SendText(string t) => Calls.Add($"TEXT {t}");
    }

    /// <summary>按入队顺序返回预设命中（或 null=未命中）的假匹配器，便于确定性测试。</summary>
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

    private static string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mhxy_ocr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePng(string dir, string name, int w = 20, int h = 20)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
    }

    // ---- 解析 ----

    [Fact]
    public void 解析_OCR_单图加TEXT_参数正确()
    {
        Assert.True(ScriptEngine.TryParse(
            "OCR a.png TEXT \"参加\" MAXERR 0.2 RETRY 3 WAIT 500 CENTER", "t",
            out ScriptProgram? p, out _));

        var ocr = Assert.IsType<OcrInstruction>(p!.Instructions[0]);
        Assert.Single(ocr.Images);
        Assert.Equal("a.png", ocr.Images[0]);
        Assert.Equal("参加", ocr.Text);
        Assert.Equal(0.2, ocr.MaxError);
        Assert.Equal(3, ocr.Retry);
        Assert.Equal(500, ocr.WaitMs);
        Assert.True(ocr.UseCenter);
    }

    [Fact]
    public void 解析_OCR_TEXT_支持带空格引号串()
    {
        Assert.True(ScriptEngine.TryParse(
            "OCR a.png TEXT \"宝图 任务\"", "t",
            out ScriptProgram? p, out _));

        var ocr = Assert.IsType<OcrInstruction>(p!.Instructions[0]);
        Assert.Equal("宝图 任务", ocr.Text);
    }

    [Fact]
    public void 解析_OCR_无图片_报错()
    {
        bool ok = ScriptEngine.TryParse("OCR MAXERR 0.2", "t", out _, out List<string>? errs);
        Assert.False(ok);
        Assert.Contains(errs!, e => e.Contains("OCR 至少需要一张图片"));
    }

    // ---- 执行：单模板无文字 ----

    [Fact]
    public async Task 执行_OCR_单模板_无文字_CENTER点击中心()
    {
        // 模板命中框 (0.4..0.6, 0.4..0.6) → 中心 (0.5,0.5) → 帧 100x200 上 (50,100)。
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png CENTER", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        Assert.Contains("DOWN 50,100", sink.Calls);
        Assert.Contains("UP 50,100", sink.Calls);
    }

    [Fact]
    public async Task 执行_OCR_单模板_无文字_区域内随机点击()
    {
        // 单图框：(0.1..0.3, 0.1..0.3) → 帧 100x200 上 x∈[10,30], y∈[20,60]
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.20, 0.20, 0.10, 0.10, 0.95));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        var down = sink.Calls.Find(c => c.StartsWith("DOWN"));
        Assert.NotNull(down);
        int x = int.Parse(down!.Split(' ')[1].Split(',')[0]);
        int y = int.Parse(down.Split(' ')[1].Split(',')[1]);
        Assert.InRange(x, 10, 30);
        Assert.InRange(y, 20, 60);
    }

    // ---- 执行：单模板 + 模板内文字定位 ----

    [Fact]
    public async Task 执行_OCR_带文字_按模板内偏移精确点击()
    {
        // 模板命中框 (0.4..0.6, 0.4..0.6)；"参加"在模板内中心 (0.75, 0.25)。
        // 文字最终坐标：fx = 0.4 + 0.75*0.2 = 0.55；fy = 0.4 + 0.25*0.2 = 0.45 → 帧 100x200 上 (55,90)。
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        var rec = new FakeTextRecognizer(new RecognizedTextLine("参加", 0.65, 0.20, 0.20, 0.10));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png TEXT \"参加\"", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir, textRecognizer: rec);

        Assert.Contains("DOWN 55,90", sink.Calls);
    }

    [Fact]
    public async Task 执行_OCR_带文字_模板内未找到_退化为模板中心()
    {
        // 识别器只返回"取消"（误差 1.0 > MAXERR 0.15），找不到"参加" → 退化点击模板中心 (0.5,0.5) → (50,100)。
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        var rec = new FakeTextRecognizer(new RecognizedTextLine("取消", 0.50, 0.50, 0.20, 0.10));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png TEXT \"参加\" CENTER", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir, textRecognizer: rec);

        Assert.Contains("DOWN 50,100", sink.Calls);
    }

    // ---- 执行：多图兼容 / 失败路径 ----

    [Fact]
    public async Task 执行_OCR_多图_只取首图作模板_其余忽略()
    {
        // 现在只支持单模板：a.png 为模板（命中 (0.25,0.25,0.1,0.1)），b.png 被忽略。
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.25, 0.25, 0.10, 0.10, 0.95));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");
        WritePng(dir, "b.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png b.png CENTER", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        Assert.Contains("DOWN 25,50", sink.Calls);
    }

    [Fact]
    public async Task 执行_OCR_模板缺失_不点击且重试()
    {
        var matcher = new FakeMatcher();

        string dir = MakeTempDir();
        // a.png 不创建

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png RETRY 2 WAIT 1", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        Assert.DoesNotContain(sink.Calls, c => c.StartsWith("DOWN"));
        Assert.True(sink.FrameFetches >= 2, $"应重试取帧，实际 {sink.FrameFetches}");
    }

    [Fact]
    public async Task 执行_OCR_全部未命中_按RETRY重试()
    {
        var matcher = new FakeMatcher(); // 空队列 → Match 返回 null

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png RETRY 3 WAIT 1", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        Assert.DoesNotContain(sink.Calls, c => c.StartsWith("DOWN"));
        Assert.Equal(3, sink.FrameFetches);
    }

    // ---- 执行：高亮回调 ----

    [Fact]
    public async Task 执行_OCR_命中时回调高亮_传模板框()
    {
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var hits = new List<(double, double, double, double)>();
        Action<double, double, double, double> highlight = (x1, y1, x2, y2) => hits.Add((x1, y1, x2, y2));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir, onOcrHighlight: highlight);

        Assert.Single(hits);
        (double x1, double y1, double x2, double y2) = hits[0];
        Assert.Equal(0.40, x1, 5);
        Assert.Equal(0.40, y1, 5);
        Assert.Equal(0.60, x2, 5);
        Assert.Equal(0.60, y2, 5);
    }

    [Fact]
    public async Task 执行_OCR_未命中_不触发高亮()
    {
        var matcher = new FakeMatcher(); // 始终未命中

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        var hits = new List<(double, double, double, double)>();
        Action<double, double, double, double> highlight = (x1, y1, x2, y2) => hits.Add((x1, y1, x2, y2));

        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png RETRY 1 WAIT 1", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir, onOcrHighlight: highlight);

        Assert.Empty(hits);
    }

    // ---- ONFAIL STOP：重试耗尽后停止脚本 ----

    [Fact]
    public void 解析_OCR_ONFAIL_STOP_置StopOnFail为true()
    {
        Assert.True(ScriptEngine.TryParse("OCR a.png ONFAIL STOP", "t", out ScriptProgram? p, out _));
        var ocr = Assert.IsType<OcrInstruction>(p!.Instructions[0]);
        Assert.True(ocr.StopOnFail);
    }

    [Fact]
    public void 解析_OCR_无ONFAIL_StopOnFail默认为false()
    {
        Assert.True(ScriptEngine.TryParse("OCR a.png", "t", out ScriptProgram? p, out _));
        var ocr = Assert.IsType<OcrInstruction>(p!.Instructions[0]);
        Assert.False(ocr.StopOnFail);
    }

    [Fact]
    public void 解析_OCR_ONFAIL_未接STOP_报错()
    {
        bool ok = ScriptEngine.TryParse("OCR a.png ONFAIL", "t", out _, out List<string>? errs);
        Assert.False(ok);
        Assert.Contains(errs!, e => e.Contains("ONFAIL"));
    }

    [Fact]
    public async Task 执行_OCR_重试耗尽_ONFAIL_STOP_抛ScriptFailStopException()
    {
        // 永不命中 + RETRY 0（首次尝试即耗尽）+ ONFAIL STOP → 抛异常停止脚本。
        var matcher = new FakeMatcher();

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        ScriptFailStopException ex = await Assert.ThrowsAsync<ScriptFailStopException>(() =>
            ScriptEngine.ExecuteAsync(
                ScriptEngine.Parse("OCR a.png RETRY 0 ONFAIL STOP", "t"), sink, 100, 200, default,
                matcher: matcher, templatesDirectory: dir));

        Assert.Contains("a.png", ex.Message);
        Assert.Contains("重试上限", ex.Message);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public async Task 执行_OCR_模板缺失_ONFAIL_STOP_抛ScriptFailStopException()
    {
        var matcher = new FakeMatcher();

        string dir = MakeTempDir();
        // a.png 不创建

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await Assert.ThrowsAsync<ScriptFailStopException>(() =>
            ScriptEngine.ExecuteAsync(
                ScriptEngine.Parse("OCR a.png RETRY 0 ONFAIL STOP", "t"), sink, 100, 200, default,
                matcher: matcher, templatesDirectory: dir));
    }

    [Fact]
    public async Task 执行_OCR_重试耗尽_无ONFAIL_继续下一步()
    {
        // 回归：无 ONFAIL 时保持原有行为——重试耗尽后静默继续，后续 TAP 仍执行。
        var matcher = new FakeMatcher(); // 永不命中

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png RETRY 0\nTAP 0.5 0.5", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        // OCR 未点击，但后续 TAP 正常执行。
        Assert.DoesNotContain(sink.Calls, c => c.StartsWith("DOWN") && c != "DOWN 50,100");
        Assert.Contains("DOWN 50,100", sink.Calls);
    }

    [Fact]
    public async Task 执行_OCR_重试内命中_ONFAIL_STOP_正常点击并继续()
    {
        // ONFAIL STOP 只影响失败路径：命中后照常点击，且不影响后续步骤。
        var matcher = new FakeMatcher();
        matcher.Responses.Enqueue(new TemplateMatch(0.50, 0.50, 0.10, 0.10, 0.95));

        string dir = MakeTempDir();
        WritePng(dir, "a.png");

        var sink = new FakeSink { Frame = new Bitmap(100, 200) };
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("OCR a.png ONFAIL STOP\nTAP 0.5 0.5", "t"), sink, 100, 200, default,
            matcher: matcher, templatesDirectory: dir);

        // OCR 命中点击 1 次 + TAP 1 次。
        Assert.Equal(2, sink.Calls.Count(c => c.StartsWith("DOWN")));
        Assert.Contains("DOWN 50,100", sink.Calls);
    }
}
