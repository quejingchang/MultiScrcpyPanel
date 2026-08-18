using System;
using System.Drawing;

using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// letterbox 坐标换算单测，对应架构文档 §5.5（逐行对应 Python 版 <c>_map_to_video</c>）。
/// <para>
/// 这些断言保证「点哪里就按到哪里」：换算一旦偏移，触摸会系统性错位，
/// 而且在真机上极难定位，所以必须在无头单测里锁死。
/// </para>
/// </summary>
public sealed class CoordinateMapperTests
{
    /// <summary>允许 ±1 像素的截断误差（Python int() 与 C# (int) 同为向零截断）。</summary>
    private static void AssertNear(int expected, int actual, int tolerance = 1)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance,
                    $"期望 {expected} ± {tolerance}，实际 {actual}");
    }

    // ---------------------------------------------------------------- 基本换算

    [Fact]
    public void 上下无黑边左右有黑边_中心点映射到视频中心()
    {
        // 控件 400x800，视频 1080x2400：
        // scale = min(400/1080, 800/2400) = 0.33333 → 显示 360x800，左右各 20px 黑边
        bool ok = CoordinateMapper.TryMapToVideo(200, 400, 400, 800, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        AssertNear(540, vx);
        AssertNear(1200, vy);
    }

    [Fact]
    public void 左右无黑边上下有黑边_中心点映射到视频中心()
    {
        // 控件 1080x3000，视频 1080x2400：scale = min(1, 1.25) = 1 → 上下各 300px 黑边
        bool ok = CoordinateMapper.TryMapToVideo(540, 1500, 1080, 3000, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        AssertNear(540, vx);
        AssertNear(1200, vy);
    }

    [Fact]
    public void 宽高比完全一致时为1比1线性映射()
    {
        // 控件 540x1200 = 视频 1080x2400 的一半
        bool ok = CoordinateMapper.TryMapToVideo(100, 200, 540, 1200, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        AssertNear(200, vx);
        AssertNear(400, vy);
    }

    [Fact]
    public void 显示区左上角映射到视频原点()
    {
        // 控件 400x800、视频 1080x2400 → offX = 20, offY = 0
        bool ok = CoordinateMapper.TryMapToVideo(20, 0, 400, 800, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        Assert.Equal(0, vx);
        Assert.Equal(0, vy);
    }

    [Fact]
    public void 显示区右下角映射到视频右下角()
    {
        bool ok = CoordinateMapper.TryMapToVideo(379, 799, 400, 800, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        AssertNear(1079, vx, 3);
        AssertNear(2399, vy, 3);
    }

    // ---------------------------------------------------------------- 黑边 clamp

    [Fact]
    public void 点在左侧黑边内_x被clamp到0()
    {
        // 控件 800x800、视频 1080x2400 → scale = 1/3，显示宽 360，offX = 220
        bool ok = CoordinateMapper.TryMapToVideo(0, 400, 800, 800, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        Assert.Equal(0, vx);
        Assert.InRange(vy, 0, 2399);
    }

    [Fact]
    public void 点在右侧黑边内_x被clamp到视频宽减一()
    {
        bool ok = CoordinateMapper.TryMapToVideo(799, 400, 800, 800, 1080, 2400, out int vx, out _);

        Assert.True(ok);
        Assert.Equal(1079, vx);
    }

    [Fact]
    public void 点在上下黑边内_y被clamp到边界()
    {
        // 控件 1080x3000、视频 1080x2400 → offY = 300
        Assert.True(CoordinateMapper.TryMapToVideo(540, 0, 1080, 3000, 1080, 2400, out _, out int topY));
        Assert.Equal(0, topY);

        Assert.True(CoordinateMapper.TryMapToVideo(540, 2999, 1080, 3000, 1080, 2400, out _, out int bottomY));
        Assert.Equal(2399, bottomY);
    }

    [Fact]
    public void 控件外的负坐标也被clamp而非返回负值()
    {
        bool ok = CoordinateMapper.TryMapToVideo(-500, -500, 400, 800, 1080, 2400, out int vx, out int vy);

        Assert.True(ok);
        Assert.Equal(0, vx);
        Assert.Equal(0, vy);
    }

    [Fact]
    public void 输出坐标恒落在视频范围内()
    {
        const int videoW = 1080;
        const int videoH = 2400;

        for (int mx = -50; mx <= 450; mx += 7)
        {
            for (int my = -50; my <= 850; my += 13)
            {
                Assert.True(CoordinateMapper.TryMapToVideo(mx, my, 400, 800, videoW, videoH, out int vx, out int vy));
                Assert.InRange(vx, 0, videoW - 1);
                Assert.InRange(vy, 0, videoH - 1);
            }
        }
    }

    // ---------------------------------------------------------------- 非法输入

    [Theory]
    [InlineData(0, 800, 1080, 2400)]   // 控件宽为 0
    [InlineData(400, 0, 1080, 2400)]   // 控件高为 0
    [InlineData(400, 800, 0, 2400)]    // 视频宽为 0（尚未收到首帧）
    [InlineData(400, 800, 1080, 0)]    // 视频高为 0
    [InlineData(-1, 800, 1080, 2400)]
    [InlineData(400, 800, -1, 2400)]
    public void 任一尺寸非正时返回false且输出负一(int ctrlW, int ctrlH, int videoW, int videoH)
    {
        bool ok = CoordinateMapper.TryMapToVideo(10, 10, ctrlW, ctrlH, videoW, videoH, out int vx, out int vy);

        Assert.False(ok);
        Assert.Equal(-1, vx);
        Assert.Equal(-1, vy);
    }

    [Fact]
    public void 一像素控件与一像素视频不崩溃()
    {
        Assert.True(CoordinateMapper.TryMapToVideo(0, 0, 1, 1, 1, 1, out int vx, out int vy));
        Assert.Equal(0, vx);
        Assert.Equal(0, vy);
    }

    // ---------------------------------------------------------------- ComputeLetterbox

    [Fact]
    public void ComputeLetterbox_左右黑边居中()
    {
        Rectangle r = CoordinateMapper.ComputeLetterbox(400, 800, 1080, 2400);

        Assert.Equal(360, r.Width);
        Assert.Equal(800, r.Height);
        Assert.Equal(20, r.X);
        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void ComputeLetterbox_上下黑边居中()
    {
        Rectangle r = CoordinateMapper.ComputeLetterbox(1080, 3000, 1080, 2400);

        Assert.Equal(1080, r.Width);
        Assert.Equal(2400, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(300, r.Y);
    }

    [Fact]
    public void ComputeLetterbox_横屏视频()
    {
        // 控件 400x800、视频 2400x1080 → scale = min(400/2400, 800/1080) = 1/6
        Rectangle r = CoordinateMapper.ComputeLetterbox(400, 800, 2400, 1080);

        Assert.Equal(400, r.Width);
        Assert.Equal(180, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(310, r.Y);
    }

    [Fact]
    public void ComputeLetterbox_目标矩形恒不超出控件()
    {
        foreach (int ctrlW in new[] { 120, 300, 640, 1000 })
        {
            foreach (int ctrlH in new[] { 200, 560, 900 })
            {
                Rectangle r = CoordinateMapper.ComputeLetterbox(ctrlW, ctrlH, 1080, 2400);

                Assert.True(r.Width >= 1 && r.Height >= 1);
                Assert.True(r.X >= 0 && r.Y >= 0);
                Assert.True(r.Right <= ctrlW, $"右边界溢出：{r} / 控件宽 {ctrlW}");
                Assert.True(r.Bottom <= ctrlH, $"下边界溢出：{r} / 控件高 {ctrlH}");
            }
        }
    }

    [Fact]
    public void ComputeLetterbox_极小控件也至少产出1x1()
    {
        Rectangle r = CoordinateMapper.ComputeLetterbox(1, 1, 1080, 2400);
        Assert.True(r.Width >= 1);
        Assert.True(r.Height >= 1);
    }

    [Theory]
    [InlineData(0, 800, 1080, 2400)]
    [InlineData(400, 0, 1080, 2400)]
    [InlineData(400, 800, 0, 2400)]
    [InlineData(400, 800, 1080, 0)]
    public void ComputeLetterbox_非法尺寸返回空矩形(int ctrlW, int ctrlH, int videoW, int videoH)
    {
        Assert.Equal(Rectangle.Empty, CoordinateMapper.ComputeLetterbox(ctrlW, ctrlH, videoW, videoH));
    }

    // ---------------------------------------------------------------- 两者一致性

    [Fact]
    public void 映射结果必须落在ComputeLetterbox给出的显示区语义内()
    {
        const int ctrlW = 400, ctrlH = 800, videoW = 1080, videoH = 2400;
        Rectangle box = CoordinateMapper.ComputeLetterbox(ctrlW, ctrlH, videoW, videoH);

        // 显示区四角在两套算法下必须自洽
        Assert.True(CoordinateMapper.TryMapToVideo(box.Left, box.Top, ctrlW, ctrlH, videoW, videoH,
                                                   out int x0, out int y0));
        Assert.Equal(0, x0);
        Assert.Equal(0, y0);

        Assert.True(CoordinateMapper.TryMapToVideo(box.Right - 1, box.Bottom - 1, ctrlW, ctrlH, videoW, videoH,
                                                   out int x1, out int y1));
        AssertNear(videoW - 1, x1, 3);
        AssertNear(videoH - 1, y1, 3);
    }
}
