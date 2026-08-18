using System;
using System.Drawing;

using MultiScrcpy.Protocol;
using MultiScrcpy.UI;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 卡片尺寸推导单测（横屏适配 Bug 回归锁）。
/// <para>
/// 锁死三件事：
/// ① 竖屏路径与横屏适配前<b>逐值一致</b>，240×600 长屏优化不得退化；
/// ② 横屏时画面区必须变成横长，且<b>比例等于设备比例</b>（letterbox 黑边趋近 0）；
/// ③ 全部缩放档（50/75/100/150/200）与常见机型比例下都成立，卡片不会退化成竖长。
/// </para>
/// </summary>
public sealed class DeviceCardLayoutTests
{
    /// <summary>配置默认的竖屏基准宽（<c>AppConfig.CardBaseWidth</c>）。</summary>
    private const int BaseW = 240;

    /// <summary>配置默认的竖屏基准高（<c>AppConfig.CardBaseHeight</c>）。</summary>
    private const int BaseH = 600;

    /// <summary>缩放档位，与 <c>MainForm.ScaleOptions</c> 一致。</summary>
    private static readonly double[] Scales = { 0.5, 0.75, 1.0, 1.5, 2.0 };

    // ---------------------------------------------------------------- 方向判定

    [Theory]
    [InlineData(1080, 2400, false)]   // 竖屏长屏
    [InlineData(2400, 1080, true)]    // 横屏
    [InlineData(1024, 472, true)]     // MaxSize=1024 限制后的横屏帧
    [InlineData(472, 1024, false)]
    [InlineData(1080, 1080, false)]   // 正方形按竖屏处理（保持旧行为）
    [InlineData(0, 0, false)]         // 方向未知
    [InlineData(-1, 100, false)]
    [InlineData(100, 0, false)]
    public void IsLandscape_只在宽严格大于高且尺寸合法时为真(int videoW, int videoH, bool expected)
    {
        Assert.Equal(expected, DeviceCardLayout.IsLandscape(videoW, videoH));
    }

    // ---------------------------------------------------------------- 竖屏不得退化

    [Fact]
    public void 竖屏卡片尺寸与横屏适配前完全一致()
    {
        foreach (double scale in Scales)
        {
            // 修复前的原始公式：max(160, base*scale) / max(280, base*scale)
            int expectedW = Math.Max(160, (int)Math.Round(BaseW * scale));
            int expectedH = Math.Max(280, (int)Math.Round(BaseH * scale));

            Size actual = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, 1080, 2400);

            Assert.Equal(expectedW, actual.Width);
            Assert.Equal(expectedH, actual.Height);
        }
    }

    [Fact]
    public void 方向未知时按竖屏处理()
    {
        Size unknown = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 0, 0);
        Size portrait = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1080, 2400);

        Assert.Equal(portrait, unknown);
        Assert.Equal(new Size(240, 600), unknown);
    }

    [Fact]
    public void 竖屏一百档画面区仍为238x508()
    {
        // 实测值：卡片 240x600 → 画面区 238x508（Padding 各占 1px，BorderStyle 不占布局像素）。
        Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1080, 2400);
        Size img = DeviceCardLayout.ComputeScreenArea(card);

        Assert.Equal(238, img.Width);
        Assert.Equal(508, img.Height);
    }

    // ---------------------------------------------------------------- 横屏必须翻转

    [Fact]
    public void 横屏时画面区变成横长()
    {
        // 1080x2340 设备横屏 + MaxSize=1024 → 帧约 1024x472
        Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1024, 472);
        Size img = DeviceCardLayout.ComputeScreenArea(card);

        Assert.True(img.Width > img.Height, $"画面区仍是竖长：{img.Width}x{img.Height}");
        Assert.True(card.Width > card.Height, $"卡片仍是竖长：{card.Width}x{card.Height}");
    }

    [Theory]
    [InlineData(1024, 472)]     // 20:9 手机横屏
    [InlineData(2400, 1080)]    // 20:9 原始分辨率
    [InlineData(1920, 1080)]    // 16:9
    [InlineData(2560, 1080)]    // 21:9 超宽
    [InlineData(1024, 768)]     // 4:3 平板横屏
    public void 横屏画面区比例与设备比例一致(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, videoW, videoH);
            Size img = DeviceCardLayout.ComputeScreenArea(card);

            double deviceRatio = (double)videoW / videoH;
            double imgRatio = (double)img.Width / img.Height;

            // 只允许整数取整带来的偏差（±1.5%）。
            Assert.True(Math.Abs(imgRatio - deviceRatio) / deviceRatio < 0.015,
                        $"scale={scale} 设备 {videoW}x{videoH} 比例 {deviceRatio:F4}，"
                        + $"画面区 {img.Width}x{img.Height} 比例 {imgRatio:F4}");

            Assert.True(img.Width > img.Height,
                        $"scale={scale} 画面区未横长：{img.Width}x{img.Height}");
        }
    }

    [Theory]
    [InlineData(1024, 472)]
    [InlineData(2400, 1080)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    [InlineData(1024, 768)]
    public void 横屏时letterbox黑边趋近于零(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, videoW, videoH);
            Size img = DeviceCardLayout.ComputeScreenArea(card);

            Rectangle box = CoordinateMapper.ComputeLetterbox(img.Width, img.Height, videoW, videoH);

            int barX = img.Width - box.Width;
            int barY = img.Height - box.Height;

            // 画面区自身就是设备比例，letterbox 只可能剩下取整级别的 1~2px。
            Assert.True(barX <= 2, $"scale={scale} 左右黑边 {barX}px（画面区 {img.Width}x{img.Height}）");
            Assert.True(barY <= 2, $"scale={scale} 上下黑边 {barY}px（画面区 {img.Width}x{img.Height}）");
        }
    }

    [Fact]
    public void 横屏修复前的旧尺寸会产生巨大黑边_作为对照()
    {
        // 对照组：固定 240x600 竖长卡片 → 画面区 238x508，塞入 1024x472 横屏帧。
        Rectangle old = CoordinateMapper.ComputeLetterbox(238, 508, 1024, 472);
        int oldBarY = 508 - old.Height;
        Assert.True(oldBarY > 300, $"对照组黑边应当很大，实际 {oldBarY}px");

        // 修复组：黑边归零。
        Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1024, 472);
        Size img = DeviceCardLayout.ComputeScreenArea(card);
        Rectangle fixedBox = CoordinateMapper.ComputeLetterbox(img.Width, img.Height, 1024, 472);
        Assert.True(img.Height - fixedBox.Height <= 2);
    }

    // ---------------------------------------------------------------- 旋转往返

    [Fact]
    public void 竖屏转横屏再转回竖屏尺寸可逆()
    {
        Size portraitBefore = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1080, 2400);
        Size landscape = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 2400, 1080);
        Size portraitAfter = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, 1.0, 1080, 2400);

        Assert.Equal(portraitBefore, portraitAfter);
        Assert.NotEqual(portraitBefore, landscape);
        Assert.True(landscape.Width > portraitBefore.Width);
        Assert.True(landscape.Height < portraitBefore.Height);
    }

    // ---------------------------------------------------------------- 下限与健壮性

    [Fact]
    public void 横屏最小缩放档画面区仍高于ScreenView下限()
    {
        // ScreenView.MinimumSize = 48x48，任何档位都不得被顶起来。
        foreach (double scale in Scales)
        {
            foreach ((int w, int h) in new[] { (1024, 472), (2560, 1080), (1920, 1080) })
            {
                Size img = DeviceCardLayout.ComputeScreenArea(
                    DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, w, h));

                Assert.True(img.Width >= 48, $"scale={scale} 画面区宽 {img.Width} < 48");
                Assert.True(img.Height >= 48, $"scale={scale} 画面区高 {img.Height} < 48");
            }
        }
    }

    [Fact]
    public void 横屏卡片高度足以容纳标题与按键区()
    {
        foreach (double scale in Scales)
        {
            Size card = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, 2560, 1080);

            Assert.True(card.Height > DeviceCardLayout.ChromeHeight,
                        $"scale={scale} 卡片高 {card.Height} 不足以容纳 chrome {DeviceCardLayout.ChromeHeight}");
            Assert.True(card.Width > DeviceCardLayout.ChromeWidth);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 非法缩放比例回落到一倍(double scale)
    {
        Size actual = DeviceCardLayout.ComputeCardSize(BaseW, BaseH, scale, 1080, 2400);
        Assert.Equal(new Size(240, 600), actual);
    }

    [Fact]
    public void 基准尺寸过小时被下限兜住()
    {
        Size card = DeviceCardLayout.ComputeCardSize(10, 10, 1.0, 1080, 2400);

        Assert.Equal(DeviceCardLayout.MinCardWidth, card.Width);
        Assert.Equal(DeviceCardLayout.MinCardHeight, card.Height);
    }

    [Fact]
    public void 基准尺寸过小时横屏也能给出可用卡片()
    {
        Size card = DeviceCardLayout.ComputeCardSize(10, 10, 1.0, 1024, 472);
        Size img = DeviceCardLayout.ComputeScreenArea(card);

        Assert.True(img.Width > img.Height);
        Assert.True(img.Width >= 48 && img.Height >= 48);
    }

    [Fact]
    public void Chrome常量与DeviceCard布局常量保持同步()
    {
        // 实测锁定：BorderStyle 不缩减客户区，只有 Padding(1) 各占 1px。
        Assert.Equal(2, DeviceCardLayout.ChromeWidth);
        Assert.Equal(2 + DeviceCard.TitleHeight + DeviceCard.ButtonAreaHeight, DeviceCardLayout.ChromeHeight);
        Assert.Equal(92, DeviceCardLayout.ChromeHeight);
    }

    [Fact]
    public void ComputeScreenArea至少产出1x1()
    {
        Size img = DeviceCardLayout.ComputeScreenArea(new Size(1, 1));
        Assert.Equal(1, img.Width);
        Assert.Equal(1, img.Height);
    }
}
