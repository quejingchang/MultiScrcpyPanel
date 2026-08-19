using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MultiScrcpy.Core.Scripting;

/// <summary>
/// 脚本「动作编排」模型：把可读的 .scr 文本与可视化步骤列表互相转换。
/// <para>
/// 用途：主程序之外的可视化编辑器用本模型把脚本呈现为可拖拽/编辑的步骤树，
/// 编辑完成后再序列化为标准 .scr 文本（与 <see cref="ScriptEngine"/> 完全兼容）。
/// </para>
/// <para>
/// 解析为「容错」模式：任何无法识别的行都会变成 <see cref="RawStep"/> 原样保留，
/// 保证打开任意旧脚本都不会丢内容、也不会因个别行出错而整体打不开。
/// </para>
/// </summary>
public static class ScriptActionModel
{
    /// <summary>把 .scr 文本解析为顶层步骤列表（LOOP 内部步骤嵌套在 <see cref="LoopStep.Children"/> 中）。</summary>
    public static List<ScriptStep> BuildSteps(string text)
    {
        var top = new List<ScriptStep>();
        var loopStack = new Stack<LoopStep>();

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string raw in lines)
        {
            string line = StripComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToUpperInvariant();

            try
            {
                if (cmd == "ENDLOOP")
                {
                    if (loopStack.Count > 0)
                    {
                        loopStack.Pop();
                    }
                    else
                    {
                        AddRaw(top, loopStack, raw.Trim());
                    }

                    continue;
                }

                ScriptStep step = cmd switch
                {
                    "ANCHOR" => ParseAnchor(parts),
                    "LOOP" => ParseLoop(parts),
                    "OCR" => ParseOcr(parts),
                    "OCR_TEXT" => ParseOcrText(raw),
                    "FIND" => ParseFind(parts),
                    "TAP" => ParseTap(parts),
                    "SWIPE" => ParseSwipe(parts),
                    "WAIT" => ParseWait(parts),
                    "KEY" => ParseKey(parts),
                    "TEXT" => new TextStep(ExtractTextBody(raw)),
                    _ => new RawStep(raw.Trim())
                };

                AddStep(top, loopStack, step);

                if (step is LoopStep lp)
                {
                    loopStack.Push(lp);
                }
            }
            catch
            {
                // 单行解析失败：保留原文，保证整体仍可打开与编辑
                AddRaw(top, loopStack, raw.Trim());
            }
        }

        // 未闭合的 LOOP：序列化时会自然补上 ENDLOOP，这里无需处理
        return top;
    }

    /// <summary>把步骤列表序列化为标准 .scr 文本（与 ScriptEngine 兼容）。</summary>
    public static string ToScript(IReadOnlyList<ScriptStep> steps)
    {
        var sb = new StringBuilder();
        foreach (ScriptStep s in steps)
        {
            Emit(s, sb, 0);
        }

        return sb.ToString();
    }

    // ---- 内部实现 ----

    private static void AddStep(List<ScriptStep> top, Stack<LoopStep> loopStack, ScriptStep step)
    {
        if (loopStack.Count > 0)
        {
            loopStack.Peek().Children.Add(step);
        }
        else
        {
            top.Add(step);
        }
    }

    private static void AddRaw(List<ScriptStep> top, Stack<LoopStep> loopStack, string raw)
    {
        AddStep(top, loopStack, new RawStep(raw));
    }

    private static void Emit(ScriptStep s, StringBuilder sb, int indent)
    {
        string pad = indent == 0 ? string.Empty : new string(' ', indent * 2);
        switch (s)
        {
            case LoopStep lp:
                sb.Append(pad).Append(lp.ToDsl()).Append('\n');
                foreach (ScriptStep c in lp.Children)
                {
                    Emit(c, sb, indent + 1);
                }

                sb.Append(pad).Append("ENDLOOP").Append('\n');
                break;

            default:
                sb.Append(pad).Append(s.ToDsl()).Append('\n');
                break;
        }
    }

    private static string StripComment(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // TEXT 文本可能含 # / //，不当注释
        if (trimmed.StartsWith("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return raw.TrimEnd();
        }

        int hashPos = raw.IndexOf('#');
        int slashPos = raw.IndexOf("//", StringComparison.Ordinal);
        int cut = hashPos;
        if (slashPos >= 0 && (cut < 0 || slashPos < cut))
        {
            cut = slashPos;
        }

        return cut < 0 ? raw.TrimEnd() : raw.Substring(0, cut).TrimEnd();
    }

    private static string ExtractTextBody(string raw)
    {
        string line = StripComment(raw).Trim();
        int idx = line.IndexOf(' ');
        if (idx < 0)
        {
            return string.Empty;
        }

        string body = line.Substring(idx + 1).Trim();
        if (body.Length >= 2 && body[0] == '"' && body[body.Length - 1] == '"')
        {
            body = body.Substring(1, body.Length - 2);
        }

        return body;
    }

    private static double Num(string token)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ||
            double.IsNaN(v) || double.IsInfinity(v))
        {
            throw new FormatException($"非法数字：{token}");
        }

        return v;
    }

    private static int Int(string token)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
        {
            throw new FormatException($"非法整数：{token}");
        }

        return v;
    }

    private static string Require(string[] parts, ref int i)
    {
        if (++i >= parts.Length)
        {
            throw new FormatException("缺少参数");
        }

        return parts[i];
    }

    private static ScriptStep ParseAnchor(string[] parts)
    {
        if (parts.Length < 4)
        {
            throw new FormatException("ANCHOR 需要 名称 x y");
        }

        return new AnchorStep(parts[1], Num(parts[2]), Num(parts[3]));
    }

    private static ScriptStep ParseLoop(string[] parts)
    {
        string arg = parts.Length > 1 ? parts[1] : "INF";
        int count = arg.Equals("INF", StringComparison.OrdinalIgnoreCase) ? 0 : Int(arg);
        return new LoopStep(count);
    }

    private static ScriptStep ParseOcr(string[] parts)
    {
        var images = new List<string>();
        double maxErr = 0.15;
        int timeout = 0;
        int retry = 1;
        int wait = 300;
        double dx = 0, dy = 0;
        bool center = false;
        bool stopOnFail = false;
        string? clickText = null;

        for (int i = 1; i < parts.Length; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "MAXERR":
                    maxErr = Num(Require(parts, ref i));
                    break;
                case "TIMEOUT":
                    timeout = Int(Require(parts, ref i));
                    break;
                case "RETRY":
                    retry = Int(Require(parts, ref i));
                    break;
                case "WAIT":
                    wait = Int(Require(parts, ref i));
                    break;
                case "DX":
                    dx = Num(Require(parts, ref i));
                    break;
                case "DY":
                    dy = Num(Require(parts, ref i));
                    break;
                case "CENTER":
                    center = true;
                    break;
                case "TEXT":
                    clickText = ParseOcrTextArg(parts, ref i);
                    break;
                case "ONFAIL":
                    if (i + 1 >= parts.Length || parts[i + 1].ToUpperInvariant() != "STOP")
                    {
                        throw new FormatException("OCR 的 ONFAIL 需要接 STOP（当前仅支持 ONFAIL STOP）");
                    }

                    stopOnFail = true;
                    i++;
                    break;
                default:
                    images.Add(parts[i]);
                    break;
            }
        }

        if (images.Count == 0)
        {
            throw new FormatException("OCR 至少需要一张图片");
        }

        return new OcrStep(images, maxErr, timeout, retry, wait, dx, dy, center, clickText, stopOnFail);
    }

    /// <summary>
    /// 解析 OCR 指令 TEXT 后的目标文字：支持带空格的引号串（"宝图 任务"）与裸词（参加）。
    /// 调用前 <paramref name="index"/> 指向 "TEXT" 本身，返回时指向文字最后一个分词。
    /// </summary>
    private static string ParseOcrTextArg(string[] parts, ref int index)
    {
        if (index + 1 >= parts.Length)
        {
            throw new FormatException("OCR 的 TEXT 缺少目标文字");
        }

        string raw = parts[++index];
        if (raw.StartsWith("\""))
        {
            var sb = new System.Text.StringBuilder(raw.Substring(1));
            while (index < parts.Length - 1 && !parts[index].EndsWith("\""))
            {
                index++;
                sb.Append(' ').Append(parts[index]);
            }

            if (sb.Length > 0 && sb[sb.Length - 1] == '"')
            {
                sb.Length--;
            }

            return sb.ToString();
        }

        return raw;
    }

    private static ScriptStep ParseOcrText(string line)
    {
        var (text, restStart) = ExtractOcrTextBody(line);
        string rest = restStart < line.Length ? line.Substring(restStart).Trim() : string.Empty;
        string[] parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var anchor = OcrTextAnchor.Center;
        double maxErr = 0.2;
        int timeout = 0;
        int retry = 1;
        int wait = 300;
        double dx = 0, dy = 0;
        bool caseSensitive = false;

        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "ANCHOR":
                    anchor = ParseOcrTextAnchor(Require(parts, ref i));
                    break;
                case "MAXERR":
                    maxErr = Num(Require(parts, ref i));
                    break;
                case "TIMEOUT":
                    timeout = Int(Require(parts, ref i));
                    break;
                case "RETRY":
                    retry = Int(Require(parts, ref i));
                    break;
                case "WAIT":
                    wait = Int(Require(parts, ref i));
                    break;
                case "DX":
                    dx = Num(Require(parts, ref i));
                    break;
                case "DY":
                    dy = Num(Require(parts, ref i));
                    break;
                case "CASE":
                    caseSensitive = true;
                    break;
                default:
                    throw new FormatException($"OCR_TEXT 未知参数：{parts[i]}");
            }
        }

        return new OcrTextStep(text, anchor, dx, dy, maxErr, timeout, retry, wait, caseSensitive);
    }

    private static (string Text, int RestStart) ExtractOcrTextBody(string line)
    {
        // line 形如：OCR_TEXT "宝图任务" ANCHOR RIGHT DX 0.05
        int cmdEnd = line.IndexOf(' ');
        if (cmdEnd < 0)
        {
            throw new FormatException("OCR_TEXT 缺少目标文字");
        }

        int i = cmdEnd + 1;
        if (i < line.Length && line[i] == '"')
        {
            int close = line.IndexOf('"', i + 1);
            if (close < 0)
            {
                throw new FormatException("OCR_TEXT 文字引号未闭合");
            }

            return (line.Substring(i + 1, close - i - 1), close + 1);
        }

        int end = line.IndexOf(' ', i);
        if (end < 0)
        {
            end = line.Length;
        }

        return (line.Substring(i, end - i), end);
    }

    private static OcrTextAnchor ParseOcrTextAnchor(string token)
    {
        return token.ToUpperInvariant() switch
        {
            "CENTER" => OcrTextAnchor.Center,
            "LEFT" => OcrTextAnchor.Left,
            "RIGHT" => OcrTextAnchor.Right,
            "TOP" => OcrTextAnchor.Top,
            "BOTTOM" => OcrTextAnchor.Bottom,
            _ => throw new FormatException($"OCR_TEXT 未知锚点：{token}")
        };
    }

    /// <summary>FIND 旧指令映射为 OCR 单图步骤（含 THEN TAP 偏移）。</summary>
    private static ScriptStep ParseFind(string[] parts)
    {
        if (parts.Length < 2)
        {
            throw new FormatException("FIND 需要图片路径");
        }

        string img = parts[1];
        double maxErr = 0.12;
        bool tap = false;
        double dx = 0, dy = 0;

        for (int i = 2; i < parts.Length; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "MAXERR":
                    maxErr = Num(Require(parts, ref i));
                    break;
                case "THEN":
                    if (i + 1 < parts.Length && parts[i + 1].ToUpperInvariant() == "TAP")
                    {
                        tap = true;
                        i++;
                        if (i + 1 < parts.Length && double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        {
                            dx = Num(parts[i + 1]);
                            if (i + 2 < parts.Length && double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            {
                                dy = Num(parts[i + 2]);
                                i += 2;
                            }
                            else
                            {
                                i++;
                            }
                        }
                    }

                    break;
            }
        }

        return new OcrStep(new List<string> { img }, maxErr, 0, 1, 300, dx, dy, tap);
    }

    private static ScriptStep ParseTap(string[] parts)
    {
        if (parts.Length < 2)
        {
            throw new FormatException("TAP 缺少坐标");
        }

        int hold = 50;
        if (parts[1].StartsWith("@", StringComparison.Ordinal))
        {
            string name = parts[1].Substring(1);
            if (parts.Length > 2)
            {
                hold = Int(parts[2]);
            }

            return new TapStep(0, 0, hold, name);
        }

        if (parts.Length < 3)
        {
            throw new FormatException("TAP 需要 x y");
        }

        double x = Num(parts[1]);
        double y = Num(parts[2]);
        if (parts.Length > 3)
        {
            hold = Int(parts[3]);
        }

        return new TapStep(x, y, hold, null);
    }

    private static ScriptStep ParseSwipe(string[] parts)
    {
        if (parts.Length < 3)
        {
            throw new FormatException("SWIPE 缺少坐标");
        }

        int dur = 300;
        if (parts[1].StartsWith("@", StringComparison.Ordinal) && parts[2].StartsWith("@", StringComparison.Ordinal))
        {
            throw new FormatException("SWIPE 锚点模式暂不支持编辑器（请改用坐标）");
        }

        if (parts.Length < 5)
        {
            throw new FormatException("SWIPE 需要 x1 y1 x2 y2");
        }

        double x1 = Num(parts[1]);
        double y1 = Num(parts[2]);
        double x2 = Num(parts[3]);
        double y2 = Num(parts[4]);
        if (parts.Length > 5)
        {
            dur = Int(parts[5]);
        }

        return new SwipeStep(x1, y1, x2, y2, dur);
    }

    private static ScriptStep ParseWait(string[] parts)
    {
        if (parts.Length < 2)
        {
            throw new FormatException("WAIT 缺少毫秒");
        }

        // 单参数：WAIT <毫秒>（固定等待）
        if (parts.Length == 2)
        {
            return new WaitStep(Int(parts[1]));
        }

        // 双参数：WAIT <最小毫秒> <最大毫秒>（范围内随机等待）
        int min = Int(parts[1]);
        int max = Int(parts[2]);
        if (max == 0 || max == min)
        {
            // 上限为 0 或等于下限：退化为固定等待
            return new WaitStep(min);
        }

        if (max < min)
        {
            throw new FormatException($"WAIT 最大毫秒({max})不能小于最小毫秒({min})");
        }

        return new WaitStep(min, max);
    }

    private static ScriptStep ParseKey(string[] parts)
    {
        if (parts.Length < 2)
        {
            throw new FormatException("KEY 缺少按键");
        }

        KeyAction action = KeyAction.Press;
        if (parts.Length > 2)
        {
            action = parts[2].ToUpperInvariant() switch
            {
                "PRESS" => KeyAction.Press,
                "DOWN" => KeyAction.Down,
                "UP" => KeyAction.Up,
                _ => throw new FormatException($"KEY 动作未知：{parts[2]}")
            };
        }

        return new KeyStep(parts[1], action);
    }
}

/// <summary>步骤类型。</summary>
public enum ScriptStepKind
{
    Ocr,
    OcrText,
    Tap,
    Swipe,
    Wait,
    Key,
    Text,
    Loop,
    Anchor,
    Raw
}

/// <summary>单个编排步骤的基类。</summary>
public abstract class ScriptStep
{
    public abstract ScriptStepKind Kind { get; }
    public abstract string ToDsl();
    public abstract string Summary { get; }

    protected static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>OCR 单模板匹配：在截图中定位模板图后点击；可选 TEXT 在模板内定位文字后精确点击（CENTER 取中心）。</summary>
public sealed class OcrStep : ScriptStep
{
    public List<string> Images { get; }
    public double MaxError { get; set; }
    public int TimeoutMs { get; set; }
    public int Retry { get; set; }
    public int WaitMs { get; set; }
    public double Dx { get; set; }
    public double Dy { get; set; }
    public bool UseCenter { get; set; }
    public string? Text { get; set; }
    /// <summary>ONFAIL STOP：重试耗尽仍未命中时停止整个脚本（默认 false = 继续下一步）。</summary>
    public bool StopOnFail { get; set; }

    public OcrStep(List<string> images, double maxError, int timeoutMs, int retry, int waitMs, double dx, double dy, bool useCenter, string? text = null, bool stopOnFail = false)
    {
        Images = images;
        MaxError = maxError;
        TimeoutMs = timeoutMs;
        Retry = retry;
        WaitMs = waitMs;
        Dx = dx;
        Dy = dy;
        UseCenter = useCenter;
        Text = text;
        StopOnFail = stopOnFail;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Ocr;

    public override string ToDsl()
    {
        var p = new List<string>(Images);
        p.Add("MAXERR");
        p.Add(Fmt(MaxError));
        if (TimeoutMs > 0)
        {
            p.Add("TIMEOUT");
            p.Add(TimeoutMs.ToString(CultureInfo.InvariantCulture));
        }

        if (Retry != 1)
        {
            p.Add("RETRY");
            p.Add(Retry.ToString(CultureInfo.InvariantCulture));
        }

        if (WaitMs != 300)
        {
            p.Add("WAIT");
            p.Add(WaitMs.ToString(CultureInfo.InvariantCulture));
        }

        if (Math.Abs(Dx) > 1e-9)
        {
            p.Add("DX");
            p.Add(Fmt(Dx));
        }

        if (Math.Abs(Dy) > 1e-9)
        {
            p.Add("DY");
            p.Add(Fmt(Dy));
        }

        if (UseCenter)
        {
            p.Add("CENTER");
        }

        if (StopOnFail)
        {
            p.Add("ONFAIL");
            p.Add("STOP");
        }

        if (!string.IsNullOrWhiteSpace(Text))
        {
            p.Add("TEXT");
            p.Add("\"" + Text + "\"");
        }

        return "OCR " + string.Join(" ", p);
    }

    public override string Summary
    {
        get
        {
            string imgs = Images.Count == 0 ? "(未选图)" : string.Join("、", Images);
            string txt = string.IsNullOrWhiteSpace(Text) ? "" : $" 文字[{Text}]";
            string mode = UseCenter ? "中心" : "随机";
            string stop = StopOnFail ? "  失败即停" : string.Empty;
            return $"OCR 识别[{imgs}]{txt}  {mode}点击  容差{MaxError:P0}{stop}";
        }
    }
}

/// <summary>
/// OCR 文字点击：对设备帧做真实文字识别，找到目标文本后按相对偏移点击。
/// <para>
/// 典型场景：在活动列表中找到"宝图任务"，然后点击其右侧的"参加"按钮。
/// 此时设置 ANCHOR RIGHT 并给 DX 一个小的正偏移即可。
/// </para>
/// </summary>
public sealed class OcrTextStep : ScriptStep
{
    public string Text { get; set; }
    public OcrTextAnchor Anchor { get; set; }
    public double Dx { get; set; }
    public double Dy { get; set; }
    public double MaxError { get; set; }
    public int TimeoutMs { get; set; }
    public int Retry { get; set; }
    public int WaitMs { get; set; }
    public bool CaseSensitive { get; set; }

    public OcrTextStep(string text, OcrTextAnchor anchor, double dx, double dy,
        double maxError, int timeoutMs, int retry, int waitMs, bool caseSensitive)
    {
        Text = text;
        Anchor = anchor;
        Dx = dx;
        Dy = dy;
        MaxError = maxError;
        TimeoutMs = timeoutMs;
        Retry = retry;
        WaitMs = waitMs;
        CaseSensitive = caseSensitive;
    }

    public override ScriptStepKind Kind => ScriptStepKind.OcrText;

    public override string ToDsl()
    {
        var sb = new StringBuilder();
        sb.Append("OCR_TEXT \"");
        sb.Append(Text.Replace("\"", "\\\""));
        sb.Append('"');
        if (Anchor != OcrTextAnchor.Center)
        {
            sb.Append(" ANCHOR ").Append(Anchor.ToString().ToUpperInvariant());
        }

        if (Math.Abs(Dx) > 1e-9)
        {
            sb.Append(" DX ").Append(Fmt(Dx));
        }

        if (Math.Abs(Dy) > 1e-9)
        {
            sb.Append(" DY ").Append(Fmt(Dy));
        }

        if (Math.Abs(MaxError - 0.2) > 1e-9)
        {
            sb.Append(" MAXERR ").Append(Fmt(MaxError));
        }

        if (TimeoutMs > 0)
        {
            sb.Append(" TIMEOUT ").Append(TimeoutMs.ToString(CultureInfo.InvariantCulture));
        }

        if (Retry != 1)
        {
            sb.Append(" RETRY ").Append(Retry.ToString(CultureInfo.InvariantCulture));
        }

        if (WaitMs != 300)
        {
            sb.Append(" WAIT ").Append(WaitMs.ToString(CultureInfo.InvariantCulture));
        }

        if (CaseSensitive)
        {
            sb.Append(" CASE");
        }

        return sb.ToString();
    }

    public override string Summary
    {
        get
        {
            string t = Text.Length > 12 ? Text.Substring(0, 12) + "…" : Text;
            string a = Anchor != OcrTextAnchor.Center ? $" [{Anchor}]" : string.Empty;
            return $"OCR 文字点击 \"{t}\"{a} 偏移({Fmt(Dx)},{Fmt(Dy)})";
        }
    }
}

/// <summary>坐标点击（归一化 0–1，或像素 &gt;1；也可引用锚点）。</summary>
public sealed class TapStep : ScriptStep
{
    public double X { get; set; }
    public double Y { get; set; }
    public int HoldMs { get; set; }
    public string? AnchorName { get; set; }

    public TapStep(double x, double y, int holdMs, string? anchorName)
        : this(anchorName, x, y, holdMs)
    {
    }

    public TapStep(string? anchorName, double x, double y, int holdMs)
    {
        AnchorName = anchorName;
        X = x;
        Y = y;
        HoldMs = holdMs;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Tap;

    public override string ToDsl()
    {
        if (!string.IsNullOrEmpty(AnchorName))
        {
            return "TAP @" + AnchorName + (HoldMs != 50 ? " " + HoldMs : string.Empty);
        }

        return "TAP " + Fmt(X) + " " + Fmt(Y) + (HoldMs != 50 ? " " + HoldMs : string.Empty);
    }

    public override string Summary =>
        string.IsNullOrEmpty(AnchorName)
            ? $"点击 ({Fmt(X)},{Fmt(Y)})  按住{HoldMs}ms"
            : $"点击 @锚点{AnchorName}  按住{HoldMs}ms";
}

/// <summary>滑动。</summary>
public sealed class SwipeStep : ScriptStep
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public int DurationMs { get; set; }

    public SwipeStep(double x1, double y1, double x2, double y2, int durationMs)
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        DurationMs = durationMs;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Swipe;

    public override string ToDsl()
    {
        string s = "SWIPE " + Fmt(X1) + " " + Fmt(Y1) + " " + Fmt(X2) + " " + Fmt(Y2);
        return DurationMs != 300 ? s + " " + DurationMs : s;
    }

    public override string Summary => $"滑动 ({Fmt(X1)},{Fmt(Y1)})→({Fmt(X2)},{Fmt(Y2)}) {DurationMs}ms";
}

/// <summary>等待（MaxMs 非 null 时表示范围内随机等待）。</summary>
public sealed class WaitStep : ScriptStep
{
    public int Ms { get; set; }
    /// <summary>范围随机等待上限；null 表示固定等待（WAIT &lt;毫秒&gt;）。</summary>
    public int? MaxMs { get; set; }

    public WaitStep(int ms, int? maxMs = null)
    {
        Ms = ms;
        MaxMs = maxMs;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Wait;

    public override string ToDsl() => MaxMs == null ? "WAIT " + Ms : $"WAIT {Ms} {MaxMs.Value}";

    public override string Summary => MaxMs == null ? $"等待 {Ms}ms" : $"等待 {Ms}~{MaxMs.Value}ms（随机）";
}

/// <summary>按键（keycode 数字或别名，如 BACK/HOME）。</summary>
public sealed class KeyStep : ScriptStep
{
    public string Key { get; set; }
    public KeyAction Action { get; set; }

    public KeyStep(string key, KeyAction action)
    {
        Key = key;
        Action = action;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Key;

    public override string ToDsl()
    {
        string s = "KEY " + Key;
        return Action != KeyAction.Press ? s + " " + Action.ToString().ToUpperInvariant() : s;
    }

    public override string Summary => $"按键 {Key} {Action}";
}

/// <summary>输入文本。</summary>
public sealed class TextStep : ScriptStep
{
    public string Text { get; set; }

    public TextStep(string text) => Text = text;

    public override ScriptStepKind Kind => ScriptStepKind.Text;

    public override string ToDsl()
    {
        string safe = Text.Replace("\"", "\\\"");
        return "TEXT \"" + safe + "\"";
    }

    public override string Summary
    {
        get
        {
            string t = Text.Length > 16 ? Text.Substring(0, 16) + "…" : Text;
            return "输入 \"" + t + "\"";
        }
    }
}

/// <summary>循环（Count=0 表示无限）。</summary>
public sealed class LoopStep : ScriptStep
{
    public int Count { get; set; }
    public List<ScriptStep> Children { get; } = new();

    public LoopStep(int count) => Count = count;

    public override ScriptStepKind Kind => ScriptStepKind.Loop;

    public override string ToDsl() => "LOOP " + (Count <= 0 ? "INF" : Count.ToString(CultureInfo.InvariantCulture));

    public override string Summary => "循环 " + (Count <= 0 ? "∞" : Count + " 次") + $"  (子步骤 {Children.Count})";
}

/// <summary>锚点定义。</summary>
public sealed class AnchorStep : ScriptStep
{
    public string Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    public AnchorStep(string name, double x, double y)
    {
        Name = name;
        X = x;
        Y = y;
    }

    public override ScriptStepKind Kind => ScriptStepKind.Anchor;

    public override string ToDsl() => "ANCHOR " + Name + " " + Fmt(X) + " " + Fmt(Y);

    public override string Summary => $"锚点 {Name} ({Fmt(X)},{Fmt(Y)})";
}

/// <summary>无法识别/保留原文的原始行（保证往返不丢内容）。</summary>
public sealed class RawStep : ScriptStep
{
    public string Raw { get; set; }

    public RawStep(string raw) => Raw = raw;

    public override ScriptStepKind Kind => ScriptStepKind.Raw;

    public override string ToDsl() => Raw;

    public override string Summary => "原文: " + (Raw.Length > 40 ? Raw.Substring(0, 40) + "…" : Raw);
}
