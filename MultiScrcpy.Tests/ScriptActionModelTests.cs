using System.Collections.Generic;
using System.Linq;
using MultiScrcpy.Core.Scripting;
using Xunit;

namespace MultiScrcpy.Tests;

public class ScriptActionModelTests
{
    private static string RoundTrip(string src)
    {
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        return ScriptActionModel.ToScript(steps);
    }

    private static void AssertParseable(string src, string tag)
    {
        bool ok = ScriptEngine.TryParse(src, tag, out _, out List<string> errors);
        Assert.True(ok, $"[{tag}] 期望可被 ScriptEngine 解析，但报错：\n" + string.Join("\n", errors));
    }

    [Fact]
    public void OCR单图步骤可解析且字段正确()
    {
        const string src = "OCR 师门任务.png MAXERR 0.15 WAIT 300 CENTER\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        Assert.Single(steps);
        var o = Assert.IsType<OcrStep>(steps[0]);
        Assert.Equal("师门任务.png", Assert.Single(o.Images));
        Assert.Equal(0.15, o.MaxError);
        Assert.True(o.UseCenter);
    }

    [Fact]
    public void OCR多图位置交集_roundtrip_可被引擎解析()
    {
        const string src = "OCR a.png b.png c.png MAXERR 0.12 TIMEOUT 5000 RETRY 3 WAIT 400 DX 0.02 DY -0.01\n";
        string outp = RoundTrip(src);
        AssertParseable(outp, "OCR多图");
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(outp);
        var o = Assert.IsType<OcrStep>(steps[0]);
        Assert.Equal(3, o.Images.Count);
        Assert.Equal(0.12, o.MaxError);
        Assert.Equal(5000, o.TimeoutMs);
        Assert.Equal(3, o.Retry);
        Assert.Equal(400, o.WaitMs);
        Assert.Equal(0.02, o.Dx);
        Assert.Equal(-0.01, o.Dy);
        Assert.False(o.UseCenter);
    }

    [Fact]
    public void 循环嵌套_roundtrip_保持子步骤()
    {
        const string src = "WAIT 200\nLOOP 3\n  OCR 宝图.png MAXERR 0.15\n  TAP 0.5 0.5\nENDLOOP\nWAIT 100\n";
        string outp = RoundTrip(src);
        AssertParseable(outp, "循环");

        List<ScriptStep> steps = ScriptActionModel.BuildSteps(outp);
        Assert.Equal(3, steps.Count); // WAIT, LOOP, WAIT
        var loop = Assert.IsType<LoopStep>(steps[1]);
        Assert.Equal(3, loop.Count);
        Assert.Equal(2, loop.Children.Count);
        Assert.IsType<OcrStep>(loop.Children[0]);
        Assert.IsType<TapStep>(loop.Children[1]);
    }

    [Fact]
    public void 旧脚本含TAP锚点与FIND_可解析_且生成文本可被引擎解析()
    {
        const string src = "ANCHOR 任务追踪 0.08 0.06\nTAP @任务追踪 80\nFIND 活动.png MAXERR 0.12 THEN TAP 0 0\nWAIT 300\n";
        string outp = RoundTrip(src);
        AssertParseable(outp, "旧脚本");

        List<ScriptStep> steps = ScriptActionModel.BuildSteps(outp);
        Assert.Contains(steps, s => s is AnchorStep a && a.Name == "任务追踪");
        Assert.Contains(steps, s => s is TapStep t && t.AnchorName == "任务追踪");
    }

    [Fact]
    public void 混合脚本_roundtrip_可被引擎解析()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("OCR 活动图标.png MAXERR 0.15 RETRY 5 WAIT 300");
        sb.AppendLine("WAIT 800");
        sb.AppendLine("OCR 师门任务.png 前往.png MAXERR 0.15 CENTER");
        sb.AppendLine("LOOP 10");
        sb.AppendLine("  OCR 完成.png MAXERR 0.18 TIMEOUT 8000 RETRY 1 WAIT 500");
        sb.AppendLine("  SWIPE 0.5 0.8 0.5 0.2 250");
        sb.AppendLine("ENDLOOP");
        sb.AppendLine("KEY BACK");
        sb.AppendLine("TEXT \"hello\"");

        string outp = RoundTrip(sb.ToString());
        AssertParseable(outp, "混合");

        // 再次解析结构应一致
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(outp);
        Assert.Equal(6, steps.Count);
        Assert.IsType<OcrStep>(steps[0]);
        Assert.IsType<WaitStep>(steps[1]);
        Assert.IsType<OcrStep>(steps[2]);
        var loop = Assert.IsType<LoopStep>(steps[3]);
        Assert.Equal(2, loop.Children.Count);
        Assert.IsType<KeyStep>(steps[4]);
        Assert.IsType<TextStep>(steps[5]);
    }

    [Fact]
    public void 空行与注释被忽略_不影响解析()
    {
        const string src = "# 这是注释\n\nOCR 师门任务.png MAXERR 0.15\n   # 行内注释\nWAIT 100\n";
        string outp = RoundTrip(src);
        AssertParseable(outp, "注释");
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(outp);
        Assert.Equal(2, steps.Count);
    }

    [Fact]
    public void 无法识别的行保留为RawStep_不丢内容()
    {
        const string src = "OCR 师门任务.png MAXERR 0.15\nFOO bar baz\nWAIT 100\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var raw = Assert.Single(steps.OfType<RawStep>());
        Assert.Equal("FOO bar baz", raw.Raw);
        // 仍能序列化为文本（FOO 作为原文保留）
        string outp = ScriptActionModel.ToScript(steps);
        Assert.Contains("FOO bar baz", outp);
    }

    [Fact]
    public void OcrStep_无图时_生成文本不被引擎接受_便于编辑器拦截()
    {
        var o = new OcrStep(new List<string>(), 0.15, 0, 1, 300, 0, 0, false);
        string dsl = o.ToDsl();
        bool ok = ScriptEngine.TryParse(dsl, "无图", out _, out List<string> errs);
        Assert.False(ok);
        Assert.Contains(errs, e => e.Contains("OCR"));
    }

    // ---- WAIT 范围随机 / OCR ONFAIL STOP 模型 ----

    [Fact]
    public void WAIT单参_roundtrip_保持固定()
    {
        const string src = "WAIT 2000\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var w = Assert.IsType<WaitStep>(steps[0]);
        Assert.Equal(2000, w.Ms);
        Assert.Null(w.MaxMs);

        string outp = RoundTrip(src);
        AssertParseable(outp, "WAIT固定");
        Assert.Contains("WAIT 2000", outp);
    }

    [Fact]
    public void WAIT双参_roundtrip_解析为范围并保留()
    {
        const string src = "WAIT 2000 5000\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var w = Assert.IsType<WaitStep>(steps[0]);
        Assert.Equal(2000, w.Ms);
        Assert.Equal(5000, w.MaxMs);
        Assert.Contains("随机", w.Summary);

        string outp = RoundTrip(src);
        AssertParseable(outp, "WAIT范围");
        Assert.Contains("WAIT 2000 5000", outp);
    }

    [Fact]
    public void WAIT双参_上下限相同_退化为固定()
    {
        const string src = "WAIT 3000 3000\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var w = Assert.IsType<WaitStep>(steps[0]);
        Assert.Equal(3000, w.Ms);
        Assert.Null(w.MaxMs);

        string outp = RoundTrip(src);
        AssertParseable(outp, "WAIT退化");
        Assert.Contains("WAIT 3000", outp);
    }

    [Fact]
    public void OCR_ONFAIL_STOP_roundtrip_保留标记()
    {
        const string src = "OCR a.png ONFAIL STOP\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var o = Assert.IsType<OcrStep>(steps[0]);
        Assert.True(o.StopOnFail);
        Assert.Contains("失败即停", o.Summary);

        string outp = RoundTrip(src);
        AssertParseable(outp, "OCR ONFAIL");
        Assert.Contains("ONFAIL STOP", outp);
    }

    [Fact]
    public void OCR_无ONFAIL_roundtrip_不输出标记()
    {
        const string src = "OCR a.png\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var o = Assert.IsType<OcrStep>(steps[0]);
        Assert.False(o.StopOnFail);

        string outp = RoundTrip(src);
        AssertParseable(outp, "OCR 无ONFAIL");
        Assert.DoesNotContain("ONFAIL", outp);
    }

    [Fact]
    public void OCR_INFINITE_roundtrip_保留标记()
    {
        const string src = "OCR a.png INFINITE\n";
        List<ScriptStep> steps = ScriptActionModel.BuildSteps(src);
        var o = Assert.IsType<OcrStep>(steps[0]);
        Assert.True(o.Infinite);
        Assert.Contains("无限重试", o.Summary);

        string outp = RoundTrip(src);
        AssertParseable(outp, "OCR INFINITE");
        Assert.Contains("INFINITE", outp);
    }
}
