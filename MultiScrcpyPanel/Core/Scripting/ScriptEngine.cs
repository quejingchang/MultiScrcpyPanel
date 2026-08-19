using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Scripting.TextRecognition;
using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core.Scripting;

/// <summary>
/// 脚本 DSL 解析与执行引擎（文本指令流）。
/// <para>
/// 设计目标：在已连接设备上自动执行一串控制动作（点按 / 滑动 / 按键 / 输入 / 等待 / 循环）。
/// 复用现有 <see cref="DeviceController"/> 控制通道，不重新实现 scrcpy 字节协议。
/// </para>
/// <para>
/// 坐标约定：<b>归一化</b> 0.0–1.0（相对设备视频帧），运行时乘以当前视频宽高；
/// 也可用 <c>@锚点</c> 引用 <c>ANCHOR</c> 定义的命名点，按设备分辨率集中校准。
/// 数值 &gt; 1.0 视为视频帧像素坐标（高级用法，跨设备不通用）。
/// </para>
/// </summary>
public static class ScriptEngine
{
    /// <summary>脚本默认目录（exe 旁的 scripts/）。</summary>
    public static string DefaultScriptsDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "scripts");
    }

    /// <summary>
    /// 把下拉里的脚本名解析为绝对路径：
    /// 绝对路径原样返回；否则视为「脚本目录下的相对路径（可含子目录）」拼到 <see cref="DefaultScriptsDirectory"/>。
    /// </summary>
    public static string ResolveScriptNameToPath(string raw)
    {
        if (Path.IsPathRooted(raw))
        {
            return raw;
        }
        // 归一化分隔符（下拉里的相对路径可能用 /），避免产生混合分隔符路径
        string rel = raw.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(DefaultScriptsDirectory(), rel);
    }

    /// <summary>
    /// 把下拉项（脚本目录相对路径）或浏览得到的绝对路径解析为「存在则」的文件全路径；
    /// 空、浏览占位符或不存则返回 null。
    /// </summary>
    public static string? ResolveScriptLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "浏览…")
        {
            return null;
        }

        string full = ResolveScriptNameToPath(raw);
        return File.Exists(full) ? full : null;
    }

    /// <summary>解析脚本文本；出错抛出 <see cref="ScriptParseException"/>。</summary>
    public static ScriptProgram Parse(string source, string? fileName = null)
    {
        if (!TryParse(source, fileName, out ScriptProgram? program, out List<string> errors))
        {
            throw new ScriptParseException(0, "解析失败：\n" + string.Join("\n", errors));
        }

        return program!;
    }

    /// <summary>解析脚本文件。</summary>
    public static ScriptProgram ParseFile(string path)
    {
        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>尝试解析；成功返回 true，失败在 errors 中给出全部错误（含文件名与行号）。</summary>
    public static bool TryParse(string source, string? fileName, out ScriptProgram? program, out List<string> errors)
    {
        program = null;
        errors = new List<string>();
        var anchors = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        var instructions = new List<ScriptInstruction>();
        var loopStack = new Stack<LoopInstruction>();
        string tag = fileName ?? "脚本";

        string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToUpperInvariant();

            try
            {
                if (cmd == "ANCHOR")
                {
                    HandleAnchor(parts, lineNo, anchors);
                    continue;
                }

                if (cmd == "ENDLOOP")
                {
                    if (loopStack.Count == 0)
                    {
                        throw new ScriptParseException(lineNo, "ENDLOOP 没有对应的 LOOP");
                    }

                    loopStack.Pop();
                    continue;
                }

                ScriptInstruction ins = ParseInstruction(cmd, parts, line, lineNo, anchors);

                if (cmd == "LOOP")
                {
                    LoopInstruction loop = (LoopInstruction)ins;
                    Route(loop, instructions, loopStack);
                    loopStack.Push(loop);
                    continue;
                }

                Route(ins, instructions, loopStack);
            }
            catch (ScriptParseException ex)
            {
                errors.Add($"[{tag}] {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"[{tag}] 第 {lineNo} 行：{ex.Message}");
            }
        }

        while (loopStack.Count > 0)
        {
            LoopInstruction unclosed = loopStack.Pop();
            errors.Add($"[{tag}] LOOP（第 {unclosed.Line} 行）缺少对应的 ENDLOOP");
        }

        if (errors.Count > 0)
        {
            return false;
        }

        program = new ScriptProgram(instructions, anchors.Keys);
        return true;
    }

    /// <summary>执行脚本：把每条指令转换为设备控制动作并发出。</summary>
    public static async Task ExecuteAsync(ScriptProgram program, IScriptDeviceSink sink,
        int videoWidth, int videoHeight, CancellationToken token,
        IProgress<ScriptLogEntry>? progress = null, string? scriptDirectory = null,
        ITemplateMatcher? matcher = null, string? templatesDirectory = null,
        Action<double, double, double, double>? onOcrHighlight = null,
        ITextRecognizer? textRecognizer = null)
    {
        matcher ??= TemplateMatcherFactory.Default;
        textRecognizer ??= TextRecognizerFactory.Default;
        await RunBlock(program.Instructions, sink, videoWidth, videoHeight, token, progress,
            scriptDirectory, matcher, templatesDirectory ?? TemplatesDirectory(), onOcrHighlight, textRecognizer);
    }

    /// <summary>模板图片默认目录（exe 旁的 templates/，对应仓库 csharp/templates）。</summary>
    public static string TemplatesDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "templates");
    }

    // ---- 内部实现 ----

    private static void Route(ScriptInstruction ins, List<ScriptInstruction> top, Stack<LoopInstruction> loopStack)
    {
        if (loopStack.Count > 0)
        {
            loopStack.Peek().Body.Add(ins);
        }
        else
        {
            top.Add(ins);
        }
    }

    private static string StripComment(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // 整行注释
        if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // TEXT 指令的文本内容可能包含 # 或 //，不能当作注释剥离
        if (trimmed.StartsWith("TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        // 行内注释：剥离首个 # 或 // 及其之后的内容
        int hashPos = raw.IndexOf('#');
        int slashPos = raw.IndexOf("//", StringComparison.Ordinal);
        int cut = hashPos;
        if (slashPos >= 0 && (cut < 0 || slashPos < cut))
        {
            cut = slashPos;
        }

        return cut < 0 ? raw : raw.Substring(0, cut).TrimEnd();
    }

    private static void HandleAnchor(string[] parts, int lineNo, Dictionary<string, (double X, double Y)> anchors)
    {
        if (parts.Length < 4)
        {
            throw new ScriptParseException(lineNo, "ANCHOR 需要：名称 x y");
        }

        string name = parts[1];
        double x = ParseCoord(parts[2], lineNo, "ANCHOR x");
        double y = ParseCoord(parts[3], lineNo, "ANCHOR y");
        anchors[name] = (x, y);
    }

    private static ScriptInstruction ParseInstruction(string cmd, string[] parts, string line,
        int lineNo, Dictionary<string, (double X, double Y)> anchors)
    {
        return cmd switch
        {
            "TAP" => ParseTap(parts, lineNo, anchors),
            "SWIPE" => ParseSwipe(parts, lineNo, anchors),
            "WAIT" => ParseWait(parts, lineNo),
            "KEY" => ParseKey(parts, lineNo),
            "TEXT" => new TextInstruction(lineNo, ExtractTextBody(line)),
            "LOOP" => ParseLoop(parts, lineNo),
            "FIND" => ParseFind(parts, lineNo),
            "OCR" => ParseOcr(parts, lineNo),
            "OCR_TEXT" => ParseOcrText(line, lineNo),
            _ => throw new ScriptParseException(lineNo, $"未知指令：{cmd}")
        };
    }

    private static TapInstruction ParseTap(string[] parts, int lineNo, Dictionary<string, (double X, double Y)> anchors)
    {
        if (parts.Length < 2)
        {
            throw new ScriptParseException(lineNo, "TAP 缺少坐标");
        }

        double x, y;
        int hold = 50;

        if (parts[1].StartsWith("@", StringComparison.Ordinal))
        {
            (x, y) = ResolveAnchor(parts[1], anchors, lineNo);
            if (parts.Length > 2)
            {
                hold = ParsePositiveInt(parts[2], lineNo, "TAP 按住时长");
            }
        }
        else
        {
            if (parts.Length < 3)
            {
                throw new ScriptParseException(lineNo, "TAP 需要 x y 两个坐标（或 TAP @锚点）");
            }

            x = ParseCoord(parts[1], lineNo, "TAP x");
            y = ParseCoord(parts[2], lineNo, "TAP y");
            if (parts.Length > 3)
            {
                hold = ParsePositiveInt(parts[3], lineNo, "TAP 按住时长");
            }
        }

        return new TapInstruction(lineNo, x, y, hold);
    }

    private static SwipeInstruction ParseSwipe(string[] parts, int lineNo, Dictionary<string, (double X, double Y)> anchors)
    {
        if (parts.Length < 3)
        {
            throw new ScriptParseException(lineNo, "SWIPE 缺少坐标");
        }

        int duration = 300;
        double x1, y1, x2, y2;

        if (parts[1].StartsWith("@", StringComparison.Ordinal) && parts[2].StartsWith("@", StringComparison.Ordinal))
        {
            (x1, y1) = ResolveAnchor(parts[1], anchors, lineNo);
            (x2, y2) = ResolveAnchor(parts[2], anchors, lineNo);
            if (parts.Length > 3)
            {
                duration = ParsePositiveInt(parts[3], lineNo, "SWIPE 时长");
            }
        }
        else
        {
            if (parts.Length < 5)
            {
                throw new ScriptParseException(lineNo, "SWIPE 需要 x1 y1 x2 y2（或 SWIPE @起点 @终点）");
            }

            x1 = ParseCoord(parts[1], lineNo, "SWIPE x1");
            y1 = ParseCoord(parts[2], lineNo, "SWIPE y1");
            x2 = ParseCoord(parts[3], lineNo, "SWIPE x2");
            y2 = ParseCoord(parts[4], lineNo, "SWIPE y2");
            if (parts.Length > 5)
            {
                duration = ParsePositiveInt(parts[5], lineNo, "SWIPE 时长");
            }
        }

        return new SwipeInstruction(lineNo, x1, y1, x2, y2, duration);
    }

    private static WaitInstruction ParseWait(string[] parts, int lineNo)
    {
        if (parts.Length < 2)
        {
            throw new ScriptParseException(lineNo, "WAIT 缺少毫秒数");
        }

        // 单参数：WAIT <毫秒>（固定等待，向后兼容）
        if (parts.Length == 2)
        {
            return new WaitInstruction(lineNo, ParsePositiveInt(parts[1], lineNo, "WAIT"));
        }

        // 双参数：WAIT <最小毫秒> <最大毫秒>（范围内随机等待）
        int min = ParsePositiveInt(parts[1], lineNo, "WAIT 最小毫秒");
        int max = ParsePositiveInt(parts[2], lineNo, "WAIT 最大毫秒");

        if (max == 0)
        {
            // 显式写 0 表示固定等待（退化为 WAIT <min>）
            return new WaitInstruction(lineNo, min);
        }

        if (max < min)
        {
            throw new ScriptParseException(lineNo, $"WAIT 最大毫秒({max})不能小于最小毫秒({min})");
        }

        if (max == min)
        {
            // 上下限相同：退化为固定等待
            return new WaitInstruction(lineNo, min);
        }

        return new WaitInstruction(lineNo, min, max);
    }

    private static KeyInstruction ParseKey(string[] parts, int lineNo)
    {
        if (parts.Length < 2)
        {
            throw new ScriptParseException(lineNo, "KEY 缺少按键名或 keycode");
        }

        KeyAction action = KeyAction.Press;
        if (parts.Length > 2)
        {
            action = parts[2].ToUpperInvariant() switch
            {
                "PRESS" => KeyAction.Press,
                "DOWN" => KeyAction.Down,
                "UP" => KeyAction.Up,
                _ => throw new ScriptParseException(lineNo, $"KEY 动作未知：{parts[2]}（应为 PRESS/DOWN/UP）")
            };
        }

        return new KeyInstruction(lineNo, ResolveKeycode(parts[1], lineNo), action);
    }

    private static LoopInstruction ParseLoop(string[] parts, int lineNo)
    {
        string arg = parts.Length > 1 ? parts[1] : "INF";
        int count = arg.Equals("INF", StringComparison.OrdinalIgnoreCase)
            ? 0
            : ParsePositiveInt(arg, lineNo, "LOOP 次数");
        return new LoopInstruction(lineNo, count);
    }

    private static (double X, double Y) ResolveAnchor(string token, Dictionary<string, (double X, double Y)> anchors, int lineNo)
    {
        string name = token.Substring(1);
        if (anchors.TryGetValue(name, out (double X, double Y) pt))
        {
            return pt;
        }

        throw new ScriptParseException(lineNo, $"未定义的锚点：{name}（请先用 ANCHOR 定义）");
    }

    private static string ExtractTextBody(string line)
    {
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

    private static double ParseCoord(string token, int lineNo, string what)
    {
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            throw new ScriptParseException(lineNo, $"{what} 不是合法数字：{token}");
        }

        if (double.IsNaN(v) || double.IsInfinity(v))
        {
            throw new ScriptParseException(lineNo, $"{what} 非法：{token}");
        }

        return v;
    }

    private static int ParsePositiveInt(string token, int lineNo, string what)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
        {
            throw new ScriptParseException(lineNo, $"{what} 需要非负整数：{token}");
        }

        return v;
    }

    private static int ResolveKeycode(string token, int lineNo)
    {
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
        {
            return code;
        }

        if (KeyAliases.TryGetValue(token, out int aliased))
        {
            return aliased;
        }

        throw new ScriptParseException(lineNo, $"未知按键：{token}（可用 keycode 数字或 HOME/BACK/... 别名）");
    }

    private static readonly Dictionary<string, int> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HOME"] = ScrcpyConstants.KEYCODE_HOME,
        ["BACK"] = ScrcpyConstants.KEYCODE_BACK,
        ["APP_SWITCH"] = ScrcpyConstants.KEYCODE_APP_SWITCH,
        ["POWER"] = ScrcpyConstants.KEYCODE_POWER,
        ["VOLUME_UP"] = ScrcpyConstants.KEYCODE_VOLUME_UP,
        ["VOLUME_DOWN"] = ScrcpyConstants.KEYCODE_VOLUME_DOWN,
        ["ENTER"] = ScrcpyConstants.KEYCODE_ENTER,
        ["MENU"] = ScrcpyConstants.KEYCODE_MENU,
        ["CAMERA"] = ScrcpyConstants.KEYCODE_CAMERA,
        ["SEARCH"] = ScrcpyConstants.KEYCODE_SEARCH,
        ["DPAD_CENTER"] = ScrcpyConstants.KEYCODE_DPAD_CENTER
    };

    private static async Task RunBlock(IReadOnlyList<ScriptInstruction> block, IScriptDeviceSink sink,
        int vw, int vh, CancellationToken token, IProgress<ScriptLogEntry>? progress,
        string? scriptDirectory = null, ITemplateMatcher? matcher = null, string? templatesDirectory = null,
        Action<double, double, double, double>? onOcrHighlight = null,
        ITextRecognizer? textRecognizer = null)
    {
        matcher ??= TemplateMatcherFactory.Default;
        foreach (ScriptInstruction ins in block)
        {
            token.ThrowIfCancellationRequested();

            switch (ins)
            {
                case TapInstruction t:
                    await RunTap(t, sink, vw, vh, progress);
                    break;

                case SwipeInstruction s:
                    await RunSwipe(s, sink, vw, vh, token, progress);
                    break;

                case WaitInstruction w:
                    if (w.MaxMs == null)
                    {
                        progress?.Report(new ScriptLogEntry(w.Line, $"WAIT {w.Ms}ms"));
                        await Delay(w.Ms, token);
                    }
                    else
                    {
                        // 范围随机等待：在 [Min, Max] 内取实际毫秒，日志输出实际值便于排查。
                        int ms = Random.Shared.Next(w.Ms, w.MaxMs.Value + 1);
                        progress?.Report(new ScriptLogEntry(w.Line, $"WAIT {w.Ms}~{w.MaxMs.Value}ms → 等待 {ms}ms"));
                        await Delay(ms, token);
                    }

                    break;

                case KeyInstruction k:
                    RunKey(k, sink);
                    progress?.Report(new ScriptLogEntry(k.Line, $"KEY {k.Keycode} {k.Action}"));
                    break;

                case TextInstruction tx:
                    sink.SendText(tx.Text);
                    progress?.Report(new ScriptLogEntry(tx.Line, $"TEXT \"{tx.Text}\""));
                    break;

                case FindInstruction fnd:
                    await RunFind(fnd, sink, vw, vh, matcher, scriptDirectory, progress, token, onOcrHighlight);
                    break;

                case OcrInstruction ocr:
                    await RunOcr(ocr, sink, vw, vh, matcher, scriptDirectory, templatesDirectory, textRecognizer, progress, token, onOcrHighlight);
                    break;

                case OcrTextInstruction ocrText:
                    await RunOcrText(ocrText, sink, vw, vh, textRecognizer, progress, token, onOcrHighlight);
                    break;

                case LoopInstruction l:
                    int iterations = l.Count <= 0 ? int.MaxValue : l.Count;
                    for (int it = 1; it <= iterations; it++)
                    {
                        token.ThrowIfCancellationRequested();
                        progress?.Report(new ScriptLogEntry(l.Line, $"LOOP 第 {it} 次 / {(l.Count <= 0 ? "∞" : l.Count.ToString(CultureInfo.InvariantCulture))}"));
                        await RunBlock(l.Body, sink, vw, vh, token, progress, scriptDirectory, matcher, templatesDirectory, onOcrHighlight, textRecognizer);
                    }

                    break;
            }
        }
    }

    private static async Task RunTap(TapInstruction t, IScriptDeviceSink sink, int vw, int vh, IProgress<ScriptLogEntry>? progress)
    {
        int x = ResolveCoord(t.X, vw);
        int y = ResolveCoord(t.Y, vh);
        sink.TouchDown(x, y);
        progress?.Report(new ScriptLogEntry(t.Line, $"TAP ({x},{y}) hold {t.HoldMs}ms"));
        await Delay(t.HoldMs, default);
        sink.TouchUp(x, y);
    }

    private static async Task RunSwipe(SwipeInstruction s, IScriptDeviceSink sink, int vw, int vh,
        CancellationToken token, IProgress<ScriptLogEntry>? progress)
    {
        int sx = ResolveCoord(s.X1, vw);
        int sy = ResolveCoord(s.Y1, vh);
        int ex = ResolveCoord(s.X2, vw);
        int ey = ResolveCoord(s.Y2, vh);

        sink.TouchDown(sx, sy);
        int steps = Math.Max(2, s.DurationMs / 30);
        int stepDelay = s.DurationMs / steps;

        for (int i = 1; i <= steps; i++)
        {
            token.ThrowIfCancellationRequested();
            double f = (double)i / steps;
            int cx = (int)(sx + (ex - sx) * f);
            int cy = (int)(sy + (ey - sy) * f);
            sink.TouchMove(cx, cy);
            await Delay(stepDelay, token);
        }

        sink.TouchUp(ex, ey);
        progress?.Report(new ScriptLogEntry(s.Line, $"SWIPE ({sx},{sy})→({ex},{ey}) {s.DurationMs}ms"));
    }

    private static void RunKey(KeyInstruction k, IScriptDeviceSink sink)
    {
        switch (k.Action)
        {
            case KeyAction.Press:
                sink.KeyPress(k.Keycode);
                break;
            case KeyAction.Down:
                sink.KeyDown(k.Keycode);
                break;
            case KeyAction.Up:
                sink.KeyUp(k.Keycode);
                break;
        }
    }

    private static int ResolveCoord(double v, int dim)
    {
        if (v < 0)
        {
            return 0;
        }

        // 归一化（0–1）：乘视频帧维度；像素（>1）：直接取整。
        int px = v <= 1.0 ? (int)(v * dim) : (int)v;
        int max = dim - 1;
        return px > max ? max : px;
    }

    private static async Task Delay(int ms, CancellationToken token)
    {
        if (ms <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(ms, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static FindInstruction ParseFind(string[] parts, int lineNo)
    {
        if (parts.Length < 2)
        {
            throw new ScriptParseException(lineNo, "FIND 需要图片路径");
        }

        string img = parts[1];
        double maxErr = 0.12;
        bool tap = false;
        double dx = 0, dy = 0;

        for (int i = 2; i < parts.Length; i++)
        {
            string t = parts[i].ToUpperInvariant();
            if (t == "MAXERR")
            {
                if (i + 1 >= parts.Length)
                {
                    throw new ScriptParseException(lineNo, "MAXERR 需要数值");
                }

                maxErr = ParseCoord(parts[i + 1], lineNo, "MAXERR");
                i++;
            }
            else if (t == "THEN")
            {
                if (i + 1 >= parts.Length || parts[i + 1].ToUpperInvariant() != "TAP")
                {
                    throw new ScriptParseException(lineNo, "FIND 的 THEN 需要接 TAP");
                }

                tap = true;
                i++; // 跳过 TAP
                if (i + 1 < parts.Length && !parts[i + 1].StartsWith("@", StringComparison.Ordinal) &&
                    double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    dx = ParseCoord(parts[i + 1], lineNo, "FIND TAP dx");
                    if (i + 2 < parts.Length && !parts[i + 2].StartsWith("@", StringComparison.Ordinal) &&
                        double.TryParse(parts[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        dy = ParseCoord(parts[i + 2], lineNo, "FIND TAP dy");
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            else
            {
                throw new ScriptParseException(lineNo, $"FIND 未知参数：{parts[i]}");
            }
        }

        return new FindInstruction(lineNo, img, maxErr, tap, dx, dy);
    }

    private static async Task RunFind(FindInstruction f, IScriptDeviceSink sink, int vw, int vh,
        ITemplateMatcher matcher, string? baseDir, IProgress<ScriptLogEntry>? progress, CancellationToken token,
        Action<double, double, double, double>? onOcrHighlight = null)
    {
        token.ThrowIfCancellationRequested();

        Bitmap? frame = sink.GetCurrentFrame();
        if (frame == null)
        {
            progress?.Report(new ScriptLogEntry(f.Line, "FIND 跳过：当前无帧"));
            return;
        }

        try
        {
            string path = Path.IsPathRooted(f.ImagePath)
                ? f.ImagePath
                : Path.Combine(baseDir ?? DefaultScriptsDirectory(), f.ImagePath);
            if (!File.Exists(path))
            {
                progress?.Report(new ScriptLogEntry(f.Line, $"FIND 跳过：图标不存在 {path}"));
                return;
            }

            using var tpl = (Bitmap)Image.FromFile(path);
            TemplateMatch? m = matcher.Match(frame, tpl, f.MaxError);
            if (m == null)
            {
                progress?.Report(new ScriptLogEntry(f.Line, $"FIND 未找到：{Path.GetFileName(path)}"));
                return;
            }

            progress?.Report(new ScriptLogEntry(f.Line, $"FIND 命中 ({m.Nx:F3},{m.Ny:F3})"));

            // ⭐ 命中标记（红框+十字+OCR 标签）叠加到画面上层，便于确认 FIND 实际命中的位置。
            onOcrHighlight?.Invoke(m.Nx - m.HalfW, m.Ny - m.HalfH, m.Nx + m.HalfW, m.Ny + m.HalfH);

            if (f.TapOnFound)
            {
                int x = ResolveCoord(Math.Clamp(m.Nx + f.TapDx, 0.0, 1.0), vw);
                int y = ResolveCoord(Math.Clamp(m.Ny + f.TapDy, 0.0, 1.0), vh);
                sink.TouchDown(x, y);
                progress?.Report(new ScriptLogEntry(f.Line, $"FIND TAP ({x},{y})"));
                await Delay(50, default);
                sink.TouchUp(x, y);
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ---- OCR 指令：多图识别 + 位置交集 + 区域内随机点击 ----

    private static OcrInstruction ParseOcr(string[] parts, int lineNo)
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
            string t = parts[i].ToUpperInvariant();
            switch (t)
            {
                case "TEXT":
                    clickText = ParseOcrTextValue(parts, ref i, lineNo);
                    break;
                case "MAXERR":
                    maxErr = ParseCoord(Require(parts, ++i, lineNo, "MAXERR"), lineNo, "MAXERR");
                    break;
                case "TIMEOUT":
                    timeout = ParsePositiveInt(Require(parts, ++i, lineNo, "TIMEOUT"), lineNo, "TIMEOUT");
                    break;
                case "RETRY":
                    retry = ParsePositiveInt(Require(parts, ++i, lineNo, "RETRY"), lineNo, "RETRY");
                    break;
                case "WAIT":
                    wait = ParsePositiveInt(Require(parts, ++i, lineNo, "WAIT"), lineNo, "WAIT");
                    break;
                case "DX":
                    dx = ParseCoord(Require(parts, ++i, lineNo, "DX"), lineNo, "DX");
                    break;
                case "DY":
                    dy = ParseCoord(Require(parts, ++i, lineNo, "DY"), lineNo, "DY");
                    break;
                case "CENTER":
                    center = true;
                    break;
                case "ONFAIL":
                    // ONFAIL STOP：重试耗尽仍未命中时停止整个脚本（默认继续下一步）。
                    if (i + 1 >= parts.Length || parts[i + 1].ToUpperInvariant() != "STOP")
                    {
                        throw new ScriptParseException(lineNo, "OCR 的 ONFAIL 需要接 STOP（当前仅支持 ONFAIL STOP）");
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
            throw new ScriptParseException(lineNo, "OCR 至少需要一张图片");
        }

        // 现在只取第一张作为模板（其余忽略，运行时给出提示）。
        return new OcrInstruction(lineNo, images, maxErr, timeout, retry, wait, dx, dy, center, clickText, stopOnFail);
    }

    /// <summary>
    /// 解析 <c>TEXT</c> 后的目标文字：支持带空格的引号串（"参加 任务"）与裸词（参加）。
    /// 调用前 <paramref name="index"/> 指向 "TEXT" 本身，返回时指向文字最后一个分词。
    /// </summary>
    private static string ParseOcrTextValue(string[] parts, ref int index, int lineNo)
    {
        if (index + 1 >= parts.Length)
        {
            throw new ScriptParseException(lineNo, "OCR 的 TEXT 缺少目标文字");
        }

        string raw = parts[++index];
        if (raw.StartsWith("\""))
        {
            var sb = new StringBuilder(raw.Substring(1));
            // 引号内可能含空格，跨多个分词拼接直至遇到结尾引号。
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

    private static OcrTextInstruction ParseOcrText(string line, int lineNo)
    {
        var (text, restStart) = ExtractOcrTextBody(line, lineNo);
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
                    anchor = ParseOcrTextAnchor(Require(parts, ++i, lineNo, "ANCHOR"), lineNo);
                    break;
                case "MAXERR":
                    maxErr = ParseCoord(Require(parts, ++i, lineNo, "MAXERR"), lineNo, "MAXERR");
                    break;
                case "TIMEOUT":
                    timeout = ParsePositiveInt(Require(parts, ++i, lineNo, "TIMEOUT"), lineNo, "TIMEOUT");
                    break;
                case "RETRY":
                    retry = ParsePositiveInt(Require(parts, ++i, lineNo, "RETRY"), lineNo, "RETRY");
                    break;
                case "WAIT":
                    wait = ParsePositiveInt(Require(parts, ++i, lineNo, "WAIT"), lineNo, "WAIT");
                    break;
                case "DX":
                    dx = ParseCoord(Require(parts, ++i, lineNo, "DX"), lineNo, "DX");
                    break;
                case "DY":
                    dy = ParseCoord(Require(parts, ++i, lineNo, "DY"), lineNo, "DY");
                    break;
                case "CASE":
                    caseSensitive = true;
                    break;
                default:
                    throw new ScriptParseException(lineNo, $"OCR_TEXT 未知参数：{parts[i]}");
            }
        }

        return new OcrTextInstruction(lineNo, text, anchor, dx, dy, maxErr, timeout, retry, wait, caseSensitive);
    }

    private static (string Text, int RestStart) ExtractOcrTextBody(string line, int lineNo)
    {
        // line 形如：OCR_TEXT "宝图任务" ANCHOR RIGHT DX 0.05
        int cmdEnd = line.IndexOf(' ');
        if (cmdEnd < 0)
        {
            throw new ScriptParseException(lineNo, "OCR_TEXT 缺少目标文字");
        }

        int i = cmdEnd + 1;
        if (i < line.Length && line[i] == '"')
        {
            int close = line.IndexOf('"', i + 1);
            if (close < 0)
            {
                throw new ScriptParseException(lineNo, "OCR_TEXT 文字引号未闭合");
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

    private static OcrTextAnchor ParseOcrTextAnchor(string token, int lineNo)
    {
        return token.ToUpperInvariant() switch
        {
            "CENTER" => OcrTextAnchor.Center,
            "LEFT" => OcrTextAnchor.Left,
            "RIGHT" => OcrTextAnchor.Right,
            "TOP" => OcrTextAnchor.Top,
            "BOTTOM" => OcrTextAnchor.Bottom,
            _ => throw new ScriptParseException(lineNo, $"OCR_TEXT 未知锚点：{token}")
        };
    }

    private static string Require(string[] parts, int idx, int lineNo, string what)
    {
        if (idx >= parts.Length)
        {
            throw new ScriptParseException(lineNo, $"OCR 的 {what} 缺少参数");
        }

        return parts[idx];
    }

    private static async Task RunOcr(OcrInstruction o, IScriptDeviceSink sink, int vw, int vh,
        ITemplateMatcher matcher, string? baseDir, string? templatesDir,
        ITextRecognizer? textRecognizer,
        IProgress<ScriptLogEntry>? progress, CancellationToken token,
        Action<double, double, double, double>? onOcrHighlight = null)
    {
        int deadline = o.TimeoutMs > 0 ? Environment.TickCount + o.TimeoutMs : 0;
        int attempt = 0;

        // OCR 现在只取第一张作为模板；若用户给了多张，运行时提示并忽略其余。
        string templateName = o.Images[0];
        string templatePath = ResolveTemplatePath(templateName, baseDir, templatesDir);
        bool multiWarned = false;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            attempt++;

            Bitmap? frame = sink.GetCurrentFrame();
            if (frame == null)
            {
                progress?.Report(new ScriptLogEntry(o.Line, "OCR 跳过：当前无帧"));
                if (!ShouldRetryOcr(o, ref deadline, ref attempt))
                {
                    HandleOcrRetryExhausted(o, templateName);
                    return;
                }

                await Delay(o.WaitMs, token);
                continue;
            }

            try
            {
                // 单模板匹配 +（可选）模板内文字定位：
                //   - 先在截图中匹配模板图片（第一张），得到其在截图中的位置与尺寸；
                //   - 若设置了点击文字，则在模板图片内定位该文字（一次性缓存相对位置），
                //     再换算为文字在截图中的最终坐标后精确点击；
                //   - 否则直接点击模板命中框（CENTER 取中心，否则随机）。
                if (!File.Exists(templatePath))
                {
                    progress?.Report(new ScriptLogEntry(o.Line,
                        $"OCR 模板缺失：{templateName}；adb截图{frame.Width}x{frame.Height}"));
                    if (!ShouldRetryOcr(o, ref deadline, ref attempt))
                    {
                        HandleOcrRetryExhausted(o, templateName);
                        return;
                    }

                    await Delay(o.WaitMs, token);
                    continue;
                }

                if (o.Images.Count > 1 && !multiWarned)
                {
                    multiWarned = true;
                    progress?.Report(new ScriptLogEntry(o.Line,
                        $"OCR 现只支持单模板图片，已忽略除 {templateName} 外的其他图片"));
                }

                using var tpl = (Bitmap)Image.FromFile(templatePath);

                // 先严格按 MAXERR 匹配模板；未命中则放宽到 1.0 取诊断分数。
                TemplateMatch? m = matcher.Match(frame, tpl, o.MaxError);
                if (m == null)
                {
                    var best = matcher.Match(frame, tpl, 1.0);
                    progress?.Report(new ScriptLogEntry(o.Line,
                        $"OCR 未命中：{Path.GetFileName(templateName)}(最佳相似度{best?.Score ?? 0:F2})；adb截图{frame.Width}x{frame.Height}"));
                    if (!ShouldRetryOcr(o, ref deadline, ref attempt))
                    {
                        HandleOcrRetryExhausted(o, templateName);
                        return;
                    }

                    await Delay(o.WaitMs, token);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(o.Text))
                {
                    if (textRecognizer == null)
                    {
                        progress?.Report(new ScriptLogEntry(o.Line,
                            $"OCR 文字引擎不可用，已退化为点击模板中心：{templateName}({m.Score:F2})"));
                        await ClickInRect(o, sink, m.Nx - m.HalfW, m.Ny - m.HalfH, m.Nx + m.HalfW, m.Ny + m.HalfH,
                            vw, vh, progress, $"OCR 模板命中 {templateName}({m.Score:F2})");
                        return;
                    }

                    (double tx, double ty)? off = await GetTemplateTextOffsetAsync(textRecognizer, templatePath, o.Text, o.MaxError, progress, templateName, o.Line, token);
                    if (off == null)
                    {
                        progress?.Report(new ScriptLogEntry(o.Line,
                            $"OCR 模板命中 {templateName}({m.Score:F2})，但模板内未找到文字\"{o.Text}\"，已退化为点击模板中心"));
                        await ClickInRect(o, sink, m.Nx - m.HalfW, m.Ny - m.HalfH, m.Nx + m.HalfW, m.Ny + m.HalfH,
                            vw, vh, progress, $"OCR 模板命中 {templateName}({m.Score:F2})（文字未找到）");
                        return;
                    }

                    // 文字在模板内的归一化位置 (tx,ty) 是相对模板左上角的比例；
                    // 模板在截图中占 (Nx±HalfW, Ny±HalfH)，故文字最终归一化坐标：
                    double fx = (m.Nx - m.HalfW) + off.Value.tx * (2.0 * m.HalfW);
                    double fy = (m.Ny - m.HalfH) + off.Value.ty * (2.0 * m.HalfH);

                    // 诊断日志：完整记录模板命中框 + 文字换算结果，便于定位"坐标算错/模板选错"。
                    // 正常路径下只多打一行（每次 OCR 命中一行），开销可忽略。
                    progress?.Report(new ScriptLogEntry(o.Line,
                        $"OCR 诊断 模板{m.HalfW:F3}x{m.HalfH:F3}@{m.Nx:F3},{m.Ny:F3} 文字{off.Value.tx:F3},{off.Value.ty:F3}→fx={fx:F3},{fy:F3} frame{frame.Width}x{frame.Height}"));

                    // 范围校验：文字点必须落在帧内 [0,1]。越界（非有限数或超出 [0,1]）说明
                    // 模板命中位置可疑（曾出现 Nx 偏离 0.466 导致 fx=0.697 落到右 70%），
                    // 记录警告并以 NaN 标记走降级路径点击模板中心，避免误点击。
                    if (!double.IsFinite(fx) || !double.IsFinite(fy) || fx < 0.0 || fx > 1.0 || fy < 0.0 || fy > 1.0)
                    {
                        progress?.Report(new ScriptLogEntry(o.Line,
                            $"OCR 警告 文字点({fx:F3},{fy:F3})越出帧范围[0,1]，模板命中({m.Nx:F3},{m.Ny:F3})可能异常，降级点击模板中心：{templateName}({m.Score:F2})"));
                        fx = double.NaN;
                        fy = double.NaN;
                    }

                    if (double.IsNaN(fx) || double.IsNaN(fy))
                    {
                        // 降级路径：点击模板命中框中心（与"模板内文字未找到"走同一路径）。
                        await ClickInRect(o, sink, m.Nx - m.HalfW, m.Ny - m.HalfH, m.Nx + m.HalfW, m.Ny + m.HalfH,
                            vw, vh, progress, $"OCR 模板命中 {templateName}({m.Score:F2})（文字越界降级）");
                        return;
                    }

                    // 高亮文字命中点（以文字点为中心的小框）。
                    const double hh = 0.012;
                    onOcrHighlight?.Invoke(fx - hh, fy - hh, fx + hh, fy + hh);

                    // 精确点击文字点（矩形退化为点，CENTER 与随机等价；DX/DY 仍生效）。
                    await ClickInRect(o, sink, fx, fy, fx, fy, vw, vh, progress,
                        $"OCR 模板命中 {templateName}({m.Score:F2}) 文字\"{o.Text}\"位置({fx:F3},{fy:F3})");
                    return;
                }

                // 无点击文字：点击模板命中框（CENTER 取中心，否则随机）。
                double x1 = m.Nx - m.HalfW, y1 = m.Ny - m.HalfH;
                double x2 = m.Nx + m.HalfW, y2 = m.Ny + m.HalfH;
                onOcrHighlight?.Invoke(x1, y1, x2, y2);
                await ClickInRect(o, sink, x1, y1, x2, y2, vw, vh, progress,
                    $"OCR 命中 1 图({Path.GetFileName(templateName)}{m.Score:F2})");
                return;
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 在模板图片内定位点击文字，返回其相对模板左上角的归一化中心 (tx, ty)（0–1）。
    /// <para>
    /// 结果按 模板路径+文字 缓存：模板是静态资源，文字位置不会随运行改变，
    /// 故只需识别一次；缓存可避免每次重试都跑一次 Tesseract。识别失败返回 null。
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, (double Tx, double Ty)?> s_templateTextCache = new();

    private static async Task<(double Tx, double Ty)?> GetTemplateTextOffsetAsync(
        ITextRecognizer rec, string templatePath, string text, double maxErr,
        IProgress<ScriptLogEntry>? progress, string templateName, int line,
        CancellationToken token)
    {
        string key = $"{templatePath}|{text}";
        if (s_templateTextCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        (double Tx, double Ty)? result = null;
        string? diag = null;
        try
        {
            using var bmp = (Bitmap)Image.FromFile(templatePath);
            IReadOnlyList<RecognizedTextLine> words = await rec.RecognizeWordsAsync(bmp, token);
            if (words.Count == 0)
            {
                diag = "Tesseract 未识别出任何词（请检查 tesseract.exe / chi_sim 语言包是否就位）";
            }
            else
            {
                diag = $"Tesseract 识别到 {words.Count} 词：{string.Join("|", words.Take(15).Select(w => "\"" + w.Text + "\""))}";
            }
            (RecognizedTextLine? box, double err) = TextMatcher.FindBestSpan(text, words);
            if (box != null && err <= maxErr)
            {
                double tx = box.CenterX;
                double ty = box.CenterY;

                // 合并中心验证：FindBestSpan 把连续词合并为包围盒。若合并中心相对首词中心
                // 偏差过大（|Δx|>0.2 或 |Δy|>0.2），说明可能误合并到不相关的词（如跨整行），改用
                // 首词中心（落在真正的"参加"字上）并告警。
                RecognizedTextLine? first = FindSpanFirstWord(words, box);
                if (first != null)
                {
                    double dx = Math.Abs(tx - first.CenterX);
                    double dy = Math.Abs(ty - first.CenterY);
                    if (dx > 0.2 || dy > 0.2)
                    {
                        progress?.Report(new ScriptLogEntry(line,
                            $"OCR 模板[{templateName}] 文字\"{text}\"合并中心({tx:F3},{ty:F3})与首词({first.CenterX:F3},{first.CenterY:F3})偏差过大(Δx={dx:F3},Δy={dy:F3})，改用首词中心"));
                        tx = first.CenterX;
                        ty = first.CenterY;
                    }
                }

                result = (tx, ty);
            }
            else
            {
                diag += $"；匹配 \"{text}\" 失败 err={err:F3}>maxErr={maxErr:F3}";
            }
        }
        catch (Exception ex)
        {
            diag = $"识别异常：{ex.GetType().Name}: {ex.Message}";
        }

        if (result == null && diag != null)
        {
            progress?.Report(new ScriptLogEntry(line, $"OCR 模板[{templateName}] 文字\"{text}\"诊断：{diag}"));
        }

        s_templateTextCache[key] = result;
        return result;
    }

        /// <summary>在指定归一化矩形区域内点击（CENTER 取中心，否则随机）。</summary>
        private static async Task ClickInRect(OcrInstruction o, IScriptDeviceSink sink,
            double x1, double y1, double x2, double y2, int vw, int vh,
            IProgress<ScriptLogEntry>? progress, string logPrefix)
        {
            // 防御：任何坐标非有限/越界时退化为帧中心（NaN 进入 ResolveCoord 会导致
            // 整数溢出或下界 0 抖动）。正常调用方会在调用前主动走降级路径，这里是兜底。
            // 注意：不夹 x1/x2/y1/y2 到 [0,1]——"模板命中框越界"本身有诊断意义，
            // 且最终的 px/py 已有 Clamp 保护；提前夹会偏移矩形中心（例如
            // 矩形 [0.89, 0.40, 1.01, 0.60] 被夹后中心变成 0.945 而非 0.95）。
            if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2))
            {
                x1 = 0.0; y1 = 0.0; x2 = 1.0; y2 = 1.0;
            }

            double px, py;
            if (o.UseCenter)
            {
                px = (x1 + x2) / 2.0;
                py = (y1 + y2) / 2.0;
            }
            else
            {
                // 区域内随机点（避免每次点同一像素被反作弊）
                px = x1 + (x2 - x1) * Random.Shared.NextDouble();
                py = y1 + (y2 - y1) * Random.Shared.NextDouble();
            }

            px = Math.Clamp(px + o.Dx, 0.0, 1.0);
            py = Math.Clamp(py + o.Dy, 0.0, 1.0);
            int ix = ResolveCoord(px, vw);
            int iy = ResolveCoord(py, vh);

            sink.TouchDown(ix, iy);
            progress?.Report(new ScriptLogEntry(o.Line, $"{logPrefix}，点击 ({ix},{iy})"));
            await Delay(50, default);
            sink.TouchUp(ix, iy);
        }

        /// <summary>
        /// 找出合并包围盒内"阅读顺序第一个"的词（Y 升序、X 升序；词中心需落在盒内）。
        /// <para>
        /// 用于 <see cref="TextMatcher.FindBestSpan"/> 合并中心验证：合并盒内的首词即为
        /// 用户实际点击的目标（如"参加"中的"参"），其位置应与合并中心接近。
        /// </para>
        /// </summary>
        private static RecognizedTextLine? FindSpanFirstWord(IReadOnlyList<RecognizedTextLine> words, RecognizedTextLine span)
        {
            const double eps = 1e-6;
            RecognizedTextLine? first = null;
            foreach (RecognizedTextLine w in words)
            {
                if (string.IsNullOrWhiteSpace(w.Text))
                {
                    continue;
                }

                if (w.CenterX < span.X - eps || w.CenterX > span.Right + eps ||
                    w.CenterY < span.Y - eps || w.CenterY > span.Bottom + eps)
                {
                    continue;
                }

                if (first == null ||
                    w.Y < first.Y - eps ||
                    (Math.Abs(w.Y - first.Y) <= eps && w.X < first.X - eps))
                {
                    first = w;
                }
            }

            return first;
        }

    private static bool ShouldRetryOcr(OcrInstruction o, ref int deadline, ref int attempt)
    {
        if (o.TimeoutMs > 0)
        {
            return Environment.TickCount < deadline;
        }

        return attempt < o.Retry;
    }

    /// <summary>
    /// OCR 重试耗尽后的收尾：若指令带 <c>ONFAIL STOP</c>，抛 <see cref="ScriptFailStopException"/>
    /// 停止整个脚本；否则什么都不做（返回后调用方继续执行下一步，向后兼容）。
    /// </summary>
    private static void HandleOcrRetryExhausted(OcrInstruction o, string templateName)
    {
        if (o.StopOnFail)
        {
            throw new ScriptFailStopException(o.Line, $"OCR 未命中 {templateName}，已达重试上限");
        }
    }

    // ---- OCR_TEXT 指令：真实文字识别 + 相对偏移点击 ----

    private static async Task RunOcrText(OcrTextInstruction o, IScriptDeviceSink sink,
        int vw, int vh, ITextRecognizer? recognizer,
        IProgress<ScriptLogEntry>? progress, CancellationToken token,
        Action<double, double, double, double>? onOcrHighlight = null)
    {
        if (recognizer == null)
        {
            progress?.Report(new ScriptLogEntry(o.Line, "OCR_TEXT 跳过：无可用文字识别引擎"));
            return;
        }

        int deadline = o.TimeoutMs > 0 ? Environment.TickCount + o.TimeoutMs : 0;
        int attempt = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            attempt++;

            Bitmap? frame = sink.GetCurrentFrame();
            if (frame == null)
            {
                progress?.Report(new ScriptLogEntry(o.Line, "OCR_TEXT 跳过：当前无帧"));
                if (!ShouldRetryOcrText(o, ref deadline, ref attempt)) return;
                await Delay(o.WaitMs, token);
                continue;
            }

            try
            {
                IReadOnlyList<RecognizedTextLine> lines = await recognizer.RecognizeAsync(frame, token);
                RecognizedTextLine? hit = TextMatcher.FindBest(o.Text, lines, o.MaxError, o.CaseSensitive);
                if (hit == null)
                {
                    var top = TextMatcher.TopCandidates(o.Text, lines, 5, o.CaseSensitive);
                    string candidates = top.Count == 0
                        ? "（OCR 未返回任何文字）"
                        : string.Join("; ", top.Select(c => $"\"{c.Text}\"({c.Error:F2})"));
                    progress?.Report(new ScriptLogEntry(o.Line, $"OCR_TEXT 未找到：\"{o.Text}\"；OCR 实际看到：{candidates}"));
                    if (!ShouldRetryOcrText(o, ref deadline, ref attempt)) return;
                    await Delay(o.WaitMs, token);
                    continue;
                }

                (double px, double py) = ResolveAnchorPoint(hit, o.Anchor);
                px = Math.Clamp(px + o.Dx, 0.0, 1.0);
                py = Math.Clamp(py + o.Dy, 0.0, 1.0);

                int ix = ResolveCoord(px, vw);
                int iy = ResolveCoord(py, vh);

                // 标记文字所在区域，方便在画面上确认识别位置。
                onOcrHighlight?.Invoke(hit.X, hit.Y, hit.Right, hit.Bottom);

                sink.TouchDown(ix, iy);
                progress?.Report(new ScriptLogEntry(o.Line, $"OCR_TEXT 命中 \"{o.Text}\" ({ix},{iy})"));
                await Delay(50, default);
                sink.TouchUp(ix, iy);
                return;
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    private static (double X, double Y) ResolveAnchorPoint(RecognizedTextLine line, OcrTextAnchor anchor)
    {
        return anchor switch
        {
            OcrTextAnchor.Left => (line.X, line.CenterY),
            OcrTextAnchor.Right => (line.Right, line.CenterY),
            OcrTextAnchor.Top => (line.CenterX, line.Y),
            OcrTextAnchor.Bottom => (line.CenterX, line.Bottom),
            _ => (line.CenterX, line.CenterY)
        };
    }

    private static bool ShouldRetryOcrText(OcrTextInstruction o, ref int deadline, ref int attempt)
    {
        if (o.TimeoutMs > 0)
        {
            return Environment.TickCount < deadline;
        }

        return attempt < o.Retry;
    }

    /// <summary>
    /// 解析 OCR 模板图片路径：绝对路径原样；否则依次尝试
    /// 脚本目录、templates 目录、默认脚本目录，命中即返回。
    /// </summary>
    private static string ResolveTemplatePath(string img, string? baseDir, string? templatesDir)
    {
        if (Path.IsPathRooted(img))
        {
            return img;
        }

        var candidates = new List<string>(3);
        if (baseDir != null) candidates.Add(Path.Combine(baseDir, img));
        if (templatesDir != null) candidates.Add(Path.Combine(templatesDir, img));
        candidates.Add(Path.Combine(DefaultScriptsDirectory(), img));

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return candidates[0];
    }
}

/// <summary>设备控制动作接收端（执行器把指令转换为具体控制事件）。便于单测用假实现替换真实 DeviceController。</summary>
public interface IScriptDeviceSink
{
    void TouchDown(int x, int y);
    void TouchMove(int x, int y);
    void TouchUp(int x, int y);
    void KeyPress(int keycode);
    void KeyDown(int keycode);
    void KeyUp(int keycode);
    void SendText(string text);
    /// <summary>
    /// 取得当前屏幕帧快照（供 FIND/OCR 指令做模板匹配）。
    /// 实现方应返回<b>adb 原始设备截图</b>，而非视频流帧或 UI 预览用的缩放帧，以保证模板匹配准确度。
    /// 无帧时返回 null。
    /// </summary>
    Bitmap? GetCurrentFrame();
}

/// <summary>把脚本动作桥接到真实 <see cref="DeviceController"/> 控制通道。</summary>
internal sealed class DeviceControllerScriptSink : IScriptDeviceSink
{
    private readonly DeviceController _controller;
    private readonly DeviceSession _session;
    private readonly int _vw;
    private readonly int _vh;

    public DeviceControllerScriptSink(DeviceController controller, DeviceSession session, int videoWidth, int videoHeight)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _vw = videoWidth;
        _vh = videoHeight;
    }

    public void TouchDown(int x, int y) => _controller.SendTouch(ScrcpyConstants.ACTION_DOWN, x, y, _vw, _vh);
    public void TouchMove(int x, int y) => _controller.SendTouch(ScrcpyConstants.ACTION_MOVE, x, y, _vw, _vh);
    public void TouchUp(int x, int y) => _controller.SendTouch(ScrcpyConstants.ACTION_UP, x, y, _vw, _vh);
    public void KeyPress(int keycode) => _controller.SendKey(keycode);
    public void KeyDown(int keycode) => _controller.SendKeycode(ScrcpyConstants.ACTION_KEY_DOWN, keycode);
    public void KeyUp(int keycode) => _controller.SendKeycode(ScrcpyConstants.ACTION_KEY_UP, keycode);
    public void SendText(string text) => _controller.SendText(text);
    public Bitmap? GetCurrentFrame() => _session.CaptureRawScreenshot();
}

/// <summary>FIND 指令：在当前屏幕帧中查找模板图标，命中后可点击其偏移位置（参加按钮）。</summary>
public sealed class FindInstruction : ScriptInstruction
{
    public string ImagePath { get; }
    public double MaxError { get; }
    public bool TapOnFound { get; }
    public double TapDx { get; }
    public double TapDy { get; }

    public FindInstruction(int line, string imagePath, double maxError, bool tapOnFound, double tapDx, double tapDy)
        : base(line)
    {
        ImagePath = imagePath;
        MaxError = maxError;
        TapOnFound = tapOnFound;
        TapDx = tapDx;
        TapDy = tapDy;
    }
}

/// <summary>
/// OCR 指令：在截图中匹配一张模板图片，命中后点击目标区域。
/// <para>
/// <b>无 TEXT</b>：直接点击模板命中框（CENTER 取中心，否则随机）。
/// </para>
/// <para>
/// <b>带 TEXT</b>：先在模板图片内定位该文字（按 模板路径+文字 缓存相对位置），
/// 再把模板在截图中的位置换算为文字的最终坐标，精确点击文字处。
/// 模板内找不到文字、或文字引擎不可用时，退化为点击模板命中框。
/// 未命中时按 TIMEOUT/RETRY 重试。
/// </para>
/// <para>
/// 语法：OCR &lt;模板图&gt; [TEXT “文字”] [MAXERR 0.15] [TIMEOUT 0] [RETRY 1] [WAIT 300] [DX n] [DY n] [CENTER] [ONFAIL STOP]
/// </para>
/// <para>
/// <b>ONFAIL STOP</b>：重试耗尽仍未命中时抛 <see cref="ScriptFailStopException"/> 停止整个脚本；
/// 未指定则保持原有行为（继续执行下一步）。
/// </para>
/// </summary>
public sealed class OcrInstruction : ScriptInstruction
{
    public IReadOnlyList<string> Images { get; }
    /// <summary>
    /// 可选的"点击文字"：设置后在模板内定位该文字，计算出文字在截图中的最终位置再点击。
    /// 为空则直接点击模板命中框（CENTER 取中心，否则随机）。
    /// </summary>
    public string? Text { get; }
    public double MaxError { get; }
    public int TimeoutMs { get; }
    public int Retry { get; }
    public int WaitMs { get; }
    public double Dx { get; }
    public double Dy { get; }
    public bool UseCenter { get; }
    /// <summary>ONFAIL STOP：重试耗尽仍未命中时停止整个脚本（默认 false = 继续下一步）。</summary>
    public bool StopOnFail { get; }

    public OcrInstruction(int line, List<string> images, double maxError, int timeoutMs, int retry, int waitMs, double dx, double dy, bool useCenter, string? text = null, bool stopOnFail = false)
        : base(line)
    {
        Images = images;
        Text = text;
        MaxError = maxError;
        TimeoutMs = timeoutMs;
        Retry = retry;
        WaitMs = waitMs;
        Dx = dx;
        Dy = dy;
        UseCenter = useCenter;
        StopOnFail = stopOnFail;
    }
}

/// <summary>
/// OCR_TEXT 指令：真实文字识别，找到目标文本后按其包围盒锚点 + 偏移点击。
/// <para>
/// 语法：OCR_TEXT "文字" [ANCHOR RIGHT] [DX 0.05] [DY 0] [MAXERR 0.2]
///        [TIMEOUT 0] [RETRY 1] [WAIT 300] [CASE]
/// </para>
/// </summary>
public sealed class OcrTextInstruction : ScriptInstruction
{
    public string Text { get; }
    public OcrTextAnchor Anchor { get; }
    public double Dx { get; }
    public double Dy { get; }
    public double MaxError { get; }
    public int TimeoutMs { get; }
    public int Retry { get; }
    public int WaitMs { get; }
    public bool CaseSensitive { get; }

    public OcrTextInstruction(int line, string text, OcrTextAnchor anchor, double dx, double dy,
        double maxError, int timeoutMs, int retry, int waitMs, bool caseSensitive)
        : base(line)
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
}

/// <summary>已解析的脚本程序（指令列表 + 定义的锚点名）。</summary>
public sealed class ScriptProgram
{
    internal ScriptProgram(List<ScriptInstruction> instructions, IEnumerable<string> anchors)
    {
        Instructions = instructions;
        Anchors = anchors.ToList();
    }

    public IReadOnlyList<ScriptInstruction> Instructions { get; }
    public IReadOnlyList<string> Anchors { get; }
}

/// <summary>脚本指令基类。</summary>
public abstract class ScriptInstruction
{
    public int Line { get; }
    protected ScriptInstruction(int line) => Line = line;
}

public sealed class TapInstruction : ScriptInstruction
{
    public double X { get; }
    public double Y { get; }
    public int HoldMs { get; }
    public TapInstruction(int line, double x, double y, int holdMs) : base(line)
    {
        X = x;
        Y = y;
        HoldMs = holdMs;
    }
}

public sealed class SwipeInstruction : ScriptInstruction
{
    public double X1 { get; }
    public double Y1 { get; }
    public double X2 { get; }
    public double Y2 { get; }
    public int DurationMs { get; }
    public SwipeInstruction(int line, double x1, double y1, double x2, double y2, int durationMs) : base(line)
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
        DurationMs = durationMs;
    }
}

public sealed class WaitInstruction : ScriptInstruction
{
    public int Ms { get; }
    /// <summary>
    /// 范围随机等待的上限毫秒；null 表示固定等待（<c>WAIT &lt;毫秒&gt;</c>）。
    /// 非 null 时实际等待 <see cref="Ms"/>~<see cref="MaxMs"/> 区间内的随机值。
    /// </summary>
    public int? MaxMs { get; }

    public WaitInstruction(int line, int ms, int? maxMs = null) : base(line)
    {
        Ms = ms;
        MaxMs = maxMs;
    }
}

public sealed class KeyInstruction : ScriptInstruction
{
    public int Keycode { get; }
    public KeyAction Action { get; }
    public KeyInstruction(int line, int keycode, KeyAction action) : base(line)
    {
        Keycode = keycode;
        Action = action;
    }
}

public sealed class TextInstruction : ScriptInstruction
{
    public string Text { get; }
    public TextInstruction(int line, string text) : base(line) => Text = text;
}

public sealed class LoopInstruction : ScriptInstruction
{
    public int Count { get; }
    public List<ScriptInstruction> Body { get; } = new();
    public LoopInstruction(int line, int count) : base(line) => Count = count;
}

/// <summary>按键动作。</summary>
public enum KeyAction
{
    Press,
    Down,
    Up
}

/// <summary>脚本解析错误（含行号）。</summary>
public sealed class ScriptParseException : Exception
{
    public int Line { get; }
    public ScriptParseException(int line, string message) : base($"第 {line} 行：{message}")
    {
        Line = line;
    }
}

/// <summary>
/// OCR 指令 <c>ONFAIL STOP</c>：重试耗尽仍未命中时抛出，表示"脚本因 OCR 失败而停止"。
/// <para>
/// 与 <see cref="OperationCanceledException"/>（用户手动停止）语义不同，调用方应单独捕获并提示。
/// </para>
/// </summary>
public sealed class ScriptFailStopException : Exception
{
    /// <summary>触发停止的指令行号。</summary>
    public int Line { get; }

    public ScriptFailStopException(int line, string message) : base($"第 {line} 行：{message}")
    {
        Line = line;
    }
}

/// <summary>执行进度日志项。</summary>
public readonly record struct ScriptLogEntry
{
    public int Line { get; }
    public string Message { get; }
    public ScriptLogEntry(int line, string message)
    {
        Line = line;
        Message = message;
    }
}
