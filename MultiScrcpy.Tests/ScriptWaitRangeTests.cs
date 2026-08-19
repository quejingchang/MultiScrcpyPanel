using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>WAIT 指令范围随机等待（WAIT &lt;最小毫秒&gt; &lt;最大毫秒&gt;）单元测试。</summary>
public class ScriptWaitRangeTests
{
    /// <summary>同步记录日志项的进度回调（避免 Progress&lt;T&gt; 的异步投递竞态）。</summary>
    private sealed class RecordingProgress : IProgress<ScriptLogEntry>
    {
        public List<ScriptLogEntry> Entries { get; } = new();
        public void Report(ScriptLogEntry value) => Entries.Add(value);
    }

    /// <summary>只记录调用、不取帧的假 sink（等待指令不依赖帧）。</summary>
    private sealed class FakeSink : IScriptDeviceSink
    {
        public List<string> Calls { get; } = new();
        public void TouchDown(int x, int y) => Calls.Add($"DOWN {x},{y}");
        public void TouchMove(int x, int y) => Calls.Add($"MOVE {x},{y}");
        public void TouchUp(int x, int y) => Calls.Add($"UP {x},{y}");
        public void KeyPress(int c) => Calls.Add($"KEYP {c}");
        public void KeyDown(int c) => Calls.Add($"KEYD {c}");
        public void KeyUp(int c) => Calls.Add($"KEYU {c}");
        public void SendText(string t) => Calls.Add($"TEXT {t}");
        public Bitmap? GetCurrentFrame() => null;
    }

    // ---- 解析 ----

    [Fact]
    public void 解析_WAIT单参_为固定等待_MaxMs为空()
    {
        Assert.True(ScriptEngine.TryParse("WAIT 2000", "t", out ScriptProgram? p, out _));
        var w = Assert.IsType<WaitInstruction>(p!.Instructions[0]);
        Assert.Equal(2000, w.Ms);
        Assert.Null(w.MaxMs);
    }

    [Fact]
    public void 解析_WAIT双参_为范围随机_MaxMs非空()
    {
        Assert.True(ScriptEngine.TryParse("WAIT 2000 5000", "t", out ScriptProgram? p, out _));
        var w = Assert.IsType<WaitInstruction>(p!.Instructions[0]);
        Assert.Equal(2000, w.Ms);
        Assert.Equal(5000, w.MaxMs);
    }

    [Fact]
    public void 解析_WAIT双参_上限等于下限_退化为固定()
    {
        Assert.True(ScriptEngine.TryParse("WAIT 3000 3000", "t", out ScriptProgram? p, out _));
        var w = Assert.IsType<WaitInstruction>(p!.Instructions[0]);
        Assert.Equal(3000, w.Ms);
        Assert.Null(w.MaxMs);
    }

    [Fact]
    public void 解析_WAIT双参_上限为0_退化为固定()
    {
        Assert.True(ScriptEngine.TryParse("WAIT 3000 0", "t", out ScriptProgram? p, out _));
        var w = Assert.IsType<WaitInstruction>(p!.Instructions[0]);
        Assert.Equal(3000, w.Ms);
        Assert.Null(w.MaxMs);
    }

    [Fact]
    public void 解析_WAIT双参_最小大于最大_报错()
    {
        bool ok = ScriptEngine.TryParse("WAIT 5000 2000", "t", out _, out List<string>? errs);
        Assert.False(ok);
        Assert.Contains(errs!, e => e.Contains("不能小于"));
    }

    [Fact]
    public void 解析_WAIT双参_第二个参数非数字_报错()
    {
        bool ok = ScriptEngine.TryParse("WAIT 2000 abc", "t", out _, out List<string>? errs);
        Assert.False(ok);
        Assert.Contains(errs!, e => e.Contains("非负整数"));
    }

    // ---- 执行 ----

    [Fact]
    public async Task 执行_WAIT固定_日志不含波浪号()
    {
        var progress = new RecordingProgress();
        await ScriptEngine.ExecuteAsync(
            ScriptEngine.Parse("WAIT 1", "t"), new FakeSink(), 100, 200, default, progress);

        var entry = Assert.Single(progress.Entries);
        Assert.Equal("WAIT 1ms", entry.Message);
        Assert.DoesNotContain("~", entry.Message);
    }

    [Fact]
    public async Task 执行_WAIT范围_日志含波浪号且实际值在区间内()
    {
        // 小范围避免拖慢测试；循环多次抽查随机性覆盖整个区间。
        for (int i = 0; i < 10; i++)
        {
            var progress = new RecordingProgress();
            await ScriptEngine.ExecuteAsync(
                ScriptEngine.Parse("WAIT 10 30", "t"), new FakeSink(), 100, 200, default, progress);

            var entry = Assert.Single(progress.Entries);
            Assert.Contains("~", entry.Message);
            Assert.Contains("等待", entry.Message);
            int actual = ExtractActualWaitMs(entry);
            Assert.InRange(actual, 10, 30);
        }
    }

    private static int ExtractActualWaitMs(ScriptLogEntry entry)
    {
        // 形如：WAIT 10~30ms → 等待 15ms
        const string marker = "等待 ";
        int idx = entry.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"日志缺少实际等待值：{entry.Message}");
        string rest = entry.Message.Substring(idx + marker.Length).Replace("ms", string.Empty).Trim();
        return int.Parse(rest, CultureInfo.InvariantCulture);
    }
}
