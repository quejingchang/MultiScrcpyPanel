using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;

using MultiScrcpy.Core.Scripting;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>脚本引擎（解析 + 执行）单元测试。</summary>
public class ScriptEngineTests
{
    /// <summary>记录控制动作调用的假 sink。</summary>
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

        public Bitmap? CurrentFrame { get; set; }
        public Bitmap? GetCurrentFrame() => CurrentFrame?.Clone(new Rectangle(0, 0, CurrentFrame.Width, CurrentFrame.Height), CurrentFrame.PixelFormat);
    }

    [Fact]
    public async Task 解析_归一化TAP_坐标按视频帧缩放()
    {
        Assert.True(ScriptEngine.TryParse("TAP 0.5 0.5 10", "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);

        Assert.Contains("DOWN 50,100", sink.Calls);
        Assert.Contains("UP 50,100", sink.Calls);
    }

    [Fact]
    public async Task 解析_锚点TAP_引用ANCHOR定义()
    {
        const string src = "ANCHOR mid 0.5 0.5\nTAP @mid\n";
        Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);

        Assert.Contains("DOWN 50,100", sink.Calls);
    }

    [Fact]
    public async Task 解析_双击锚点SWIPE_起终点与插值()
    {
        const string src = "ANCHOR a 0 0\nANCHOR b 1 1\nSWIPE @a @b 100\n";
        Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);

        Assert.Contains("DOWN 0,0", sink.Calls);
        // 归一化 1.0 钳到合法像素上界 dim-1
        Assert.Contains("UP 99,199", sink.Calls);
        Assert.True(sink.Calls.Exists(c => c.StartsWith("MOVE ")), "应产生中间 MOVE 事件");
    }

    [Fact]
    public async Task 解析_LOOP_重复执行子指令()
    {
        const string src = "LOOP 3\nTAP 0.5 0.5\nENDLOOP\n";
        Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);

        Assert.Equal(3, sink.Calls.Count(c => c.StartsWith("DOWN")));
    }

    [Fact]
    public async Task 解析_KEY别名_HOME_映射为keycode_3()
    {
        Assert.True(ScriptEngine.TryParse("KEY HOME", "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);

        Assert.Contains("KEYP 3", sink.Calls);
    }

    [Theory]
    [InlineData("TAP @noanchor", "未定义的锚点")]
    [InlineData("LOOP 2\nTAP 0.5 0.5\n", "ENDLOOP")]
    [InlineData("FOO 1 2", "未知指令")]
    [InlineData("TAP abc 0.5", "TAP x")]
    public void 解析_错误脚本_返回false并给出原因(string src, string expect)
    {
        bool ok = ScriptEngine.TryParse(src, "t", out _, out List<string>? errs);
        Assert.False(ok);
        Assert.Contains(errs!, e => e.Contains(expect));
    }

    [Fact]
    public async Task 执行_取消令牌_立即抛出OperationCanceledException()
    {
        const string src = "LOOP INF\nWAIT 10000\nENDLOOP\n";
        Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ScriptEngine.ExecuteAsync(p!, new FakeSink(), 100, 200, cts.Token));
    }

    [Fact]
    public void 解析_行内井号注释_被忽略()
    {
        // 此前所有任务脚本第 19 行形如「TAP @任务追踪  # 说明」，曾被误当按住时长而解析失败
        const string src = "ANCHOR 任务追踪 0.50 0.35\nTAP @任务追踪          # 选中活动面板中第一个/推荐任务";
        Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out List<string>? errs));
        Assert.Empty(errs!);
        Assert.Single(p!.Instructions);
    }

    [Fact]
    public async Task 解析_TEXT行内井号_不当作注释()
    {
        // TEXT 的文本内容可能包含 #，不应被剥离
        Assert.True(ScriptEngine.TryParse("TEXT 你好#世界", "t", out ScriptProgram? p, out _));
        var sink = new FakeSink();
        await ScriptEngine.ExecuteAsync(p!, sink, 100, 200, default);
        Assert.Contains("TEXT 你好#世界", sink.Calls);
    }

    [Fact]
    public async Task 执行_FIND命中_点击命中中心()
    {
        // 合成帧：灰底 + 中央红方块（即「图标」）
        using var frame = new Bitmap(240, 480, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Gray);
            g.FillRectangle(Brushes.Red, 100, 200, 40, 40); // 中心 (120,220)
        }

        string tplPath = Path.Combine(Path.GetTempPath(), $"mhxy_tpl_{Guid.NewGuid():N}.png");
        using (var tpl = new Bitmap(40, 40, PixelFormat.Format24bppRgb))
        {
            using var g = Graphics.FromImage(tpl);
            g.Clear(Color.Red);
            tpl.Save(tplPath, ImageFormat.Png);
        }

        try
        {
            string src = $"FIND {tplPath} THEN TAP 0 0";
            Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
            var sink = new FakeSink { CurrentFrame = frame };
            await ScriptEngine.ExecuteAsync(p!, sink, 240, 480, default);

            Assert.Contains(sink.Calls, c => c.StartsWith("DOWN"));
            Assert.Contains(sink.Calls, c => c.StartsWith("UP"));
        }
        finally
        {
            File.Delete(tplPath);
        }
    }

    [Fact]
    public async Task 执行_FIND未找到_不抛错且未点击()
    {
        using var frame = new Bitmap(240, 480, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Gray);
        }

        string tplPath = Path.Combine(Path.GetTempPath(), $"mhxy_tpl_{Guid.NewGuid():N}.png");
        using (var tpl = new Bitmap(40, 40, PixelFormat.Format24bppRgb))
        {
            using var g = Graphics.FromImage(tpl);
            g.Clear(Color.Red);
            tpl.Save(tplPath, ImageFormat.Png);
        }

        try
        {
            string src = $"FIND {tplPath}";
            Assert.True(ScriptEngine.TryParse(src, "t", out ScriptProgram? p, out _));
            var sink = new FakeSink { CurrentFrame = frame };
            await ScriptEngine.ExecuteAsync(p!, sink, 240, 480, default); // 不应抛异常

            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("DOWN"));
        }
        finally
        {
            File.Delete(tplPath);
        }
    }
}
