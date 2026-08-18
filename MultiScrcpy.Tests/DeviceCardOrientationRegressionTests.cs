using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Protocol;
using MultiScrcpy.UI;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 「横屏不翻转」Bug 的<b>运行态端到端回归锁</b>（QA 独立补强，与 <see cref="DeviceCardLayoutTests"/> 互补）。
/// <para>
/// <see cref="DeviceCardLayoutTests"/> 只验证 <see cref="DeviceCardLayout"/> 这层<b>纯几何推导</b>；
/// 本类把<b>真实 WinForms 控件树</b>（<see cref="DeviceCard"/> → <c>TableLayoutPanel</c> →
/// <see cref="ScreenView"/>）拉起来<b>并创建窗口句柄</b>，测量的是运行态真实像素。
/// </para>
/// <para>
/// ⚠️ <b>为什么必须创建句柄</b>：<c>UserControl.BorderStyle = FixedSingle</c> 的边框
/// <b>只有在句柄创建后</b>才会缩减客户区（<c>WS_BORDER</c>）。不建句柄时
/// <c>ClientSize == Size</c>，测出来的画面区会比运行态大 2px，据此校准常量会得到错误结论。
/// 本类一律 <c>_ = card.Handle</c> 后再测量。
/// </para>
/// <para>全部用例<b>不启动消息循环、不接触 socket / FFmpeg</b>，可无头执行。</para>
/// </summary>
public sealed class DeviceCardOrientationRegressionTests : IDisposable
{
    /// <summary>缩放档位，与 <c>MainForm.ScaleOptions</c> 一致。</summary>
    private static readonly double[] Scales = { 0.5, 0.75, 1.0, 1.5, 2.0 };

    /// <summary>常见横屏帧尺寸（含 MaxSize 限制后的实际帧）。</summary>
    public static TheoryData<int, int> LandscapeFrames => new()
    {
        { 1024, 472 },   // 20:9 手机横屏（MaxSize=1024）
        { 2400, 1080 },  // 20:9 原始
        { 1920, 1080 },  // 16:9
        { 2560, 1080 },  // 21:9 超宽
        { 1024, 768 },   // 4:3 平板
        { 1280, 800 },   // 16:10 平板
    };

    /// <summary>常见竖屏帧尺寸。</summary>
    public static TheoryData<int, int> PortraitFrames => new()
    {
        { 1080, 2400 },
        { 1080, 1920 },
        { 720, 1600 },
        { 1440, 3200 },
    };

    private readonly AppConfig _cfg = new();
    private readonly DeviceManager _manager;
    private readonly List<Control> _owned = new();

    /// <summary>建立无头可用的管理器（adb 路径留空 → 不会拉起任何进程）。</summary>
    public DeviceCardOrientationRegressionTests()
    {
        _manager = new DeviceManager(_cfg, new AdbClient(string.Empty));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Control c in _owned)
        {
            c.Dispose();
        }

        _manager.Dispose();
    }

    // ============================================================ 验收点 (a) 横屏翻转 + 铺满 + 不变形

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 验收A1_横屏各档位画面区必须翻成横长(int videoW, int videoH)
    {
        // ⭐ 验收点：横屏帧的「画面区（ScreenView）」必须等于设备比例（横长）。
        //    这才是本次 Bug 的真正修复目标——旧实现把横屏帧压进竖长 240x600 画面区 → 上下大黑边。
        //
        // ⚠️ 关于<b>卡片外框</b>：外框是否「看起来也横长」取决于视频宽高比与缩放档位。
        //    标题栏(26) + 按键区(64) 是固定 ~92px 的竖直开销；当视频接近正方形（如 4:3）且缩放很低时，
        //    这张 92px 的开销会让<b>外框</b>仍偏竖长（这是几何必然，不是 Bug），
        //    但只要<b>画面区</b>已是横长且比例贴合设备（见 验收A2/A3），视频就铺满不变形，Bug 即已修复。
        //    因此本用例只断言「画面区横长 + 已脱离旧写死的 240x600 竖长基准」。
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(videoW, videoH, scale);

            Assert.True(FindScreen(card).Width > FindScreen(card).Height,
                        $"{videoW}x{videoH} @{scale}：画面区仍是竖长 {FindScreen(card).Width}x{FindScreen(card).Height}");

            // 旧实现下横屏卡片也会卡在 240x600；此处确认已脱离该竖长基准（修复确实生效）。
            Assert.NotEqual(new Size(240, 600), card.Size);
        }
    }

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 验收A2_横屏运行态不得再出现上下大黑边(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(videoW, videoH, scale);
            ScreenView screen = FindScreen(card);
            Rectangle box = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, videoW, videoH);

            int barY = screen.Height - box.Height;
            int barX = screen.Width - box.Width;

            // ⭐ 这是本次 Bug 的核心症状：横屏时上下必须彻底没有黑边。
            Assert.True(barY == 0, $"{videoW}x{videoH} @{scale}：上下黑边 {barY}px（画面区 {screen.Width}x{screen.Height}）");

            // 左右只允许取整级别的残余（当前实现实测最大 4px，见 QA 报告的 Chrome 常量问题）。
            Assert.True(barX <= 5, $"{videoW}x{videoH} @{scale}：左右黑边 {barX}px（画面区 {screen.Width}x{screen.Height}）");
        }
    }

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 验收A3_横屏画面不变形(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(videoW, videoH, scale);
            ScreenView screen = FindScreen(card);
            Rectangle box = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, videoW, videoH);

            double deviceRatio = (double)videoW / videoH;
            double boxRatio = (double)box.Width / box.Height;

            Assert.True(Math.Abs(boxRatio - deviceRatio) / deviceRatio < 0.02,
                        $"{videoW}x{videoH} @{scale}：绘制矩形 {box.Width}x{box.Height} 比例 {boxRatio:F4} 偏离设备 {deviceRatio:F4}");
        }
    }

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 验收A4_横屏可视面积显著大于修复前(int videoW, int videoH)
    {
        // 修复前：卡片恒为 240x600，运行态画面区 236x506（竖长），横屏帧只能缩得很小。
        Rectangle before = CoordinateMapper.ComputeLetterbox(236, 506, videoW, videoH);

        DeviceCard card = NewCard(videoW, videoH, 1.0);
        ScreenView screen = FindScreen(card);
        Rectangle after = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, videoW, videoH);

        Assert.True((long)after.Width * after.Height > (long)before.Width * before.Height,
                    $"{videoW}x{videoH}：修复后可视面积 {after.Width}x{after.Height} 未大于修复前 {before.Width}x{before.Height}");
    }

    // ============================================================ 验收点 (b) 竖屏不退化

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 验收B1_竖屏各档位与修复前原始公式逐值一致(double scale)
    {
        DeviceCard card = NewCard(1080, 2400, scale);

        // 修复前的 ApplyScale 原文：max(160, round(base*scale)) / max(280, round(base*scale))。
        int expectedW = Math.Max(160, (int)Math.Round(_cfg.CardBaseWidth * scale));
        int expectedH = Math.Max(280, (int)Math.Round(_cfg.CardBaseHeight * scale));

        Assert.Equal(new Size(expectedW, expectedH), card.Size);
    }

    [Theory]
    [MemberData(nameof(PortraitFrames))]
    public void 验收B2_任意竖屏机型都保持240x600长屏基准(int videoW, int videoH)
    {
        DeviceCard card = NewCard(videoW, videoH, 1.0);

        Assert.Equal(new Size(_cfg.CardBaseWidth, _cfg.CardBaseHeight), card.Size);
        Assert.True(card.Height > card.Width, "竖屏卡片必须保持长屏形状");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void 验收B3_方向未知时各档位仍走竖屏路径(double scale)
    {
        DeviceCard unknown = NewCard(0, 0, scale);
        DeviceCard portrait = NewCard(1080, 2400, scale);

        Assert.Equal(portrait.Size, unknown.Size);
        Assert.Equal(FindScreen(portrait).Size, FindScreen(unknown).Size);
    }

    [Fact]
    public void 验收B4_竖屏画面同样没有上下黑边()
    {
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(1080, 2400, scale);
            ScreenView screen = FindScreen(card);
            Rectangle box = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, 1080, 2400);

            // 竖长画面区里塞 9:20 帧：上下应当贴满，左右允许留白（这是长屏优化的既有行为）。
            Assert.True(screen.Height - box.Height <= 2 || screen.Width - box.Width <= 2,
                        $"@{scale} 画面区 {screen.Width}x{screen.Height} 与帧比例严重不匹配");
        }
    }

    // ============================================================ 验收点 (c) 旋转往返

    [Fact]
    public void 验收C1_旋转往返卡片与画面区尺寸均可逆()
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        ScreenView screen = FindScreen(card);

        Size portraitCard = card.Size;
        Size portraitScreen = screen.Size;

        card.ApplyOrientation(2400, 1080);
        Assert.True(card.Width > card.Height, "旋转到横屏后卡片未翻转");

        card.ApplyOrientation(1080, 2400);

        Assert.Equal(portraitCard, card.Size);
        Assert.Equal(portraitScreen, screen.Size);
    }

    [Fact]
    public void 验收C2_连续多次旋转不累积漂移()
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        Size portrait = card.Size;
        Size landscapeFirst = Size.Empty;

        for (int i = 0; i < 5; i++)
        {
            card.ApplyOrientation(2400, 1080);
            if (i == 0)
            {
                landscapeFirst = card.Size;
            }

            Assert.Equal(landscapeFirst, card.Size);

            card.ApplyOrientation(1080, 2400);
            Assert.Equal(portrait, card.Size);
        }
    }

    [Fact]
    public void 验收C3_先缩放后旋转与先旋转后缩放结果一致()
    {
        foreach (double scale in Scales)
        {
            DeviceCard a = NewCard(1080, 2400, 1.0);
            a.ApplyScale(scale);
            a.ApplyOrientation(1920, 1080);

            DeviceCard b = NewCard(1080, 2400, 1.0);
            b.ApplyOrientation(1920, 1080);
            b.ApplyScale(scale);

            Assert.Equal(a.Size, b.Size);
            Assert.Equal(FindScreen(a).Size, FindScreen(b).Size);
        }
    }

    // ============================================================ 根因回归：旋转事件驱动重排

    [Fact]
    public void 根因回归_ResolutionChanged事件端到端翻转卡片()
    {
        // Arrange：竖屏起步的卡片 + 已绑定会话。
        var info = new DeviceInfo("qa-rotate", DeviceState.Streaming, "QA")
        {
            VideoWidth = 1080,
            VideoHeight = 2400
        };
        DeviceCard card = NewCard(info, 1.0);
        using DeviceSession session = NewSession(info);
        card.Bind(session);

        Assert.Equal(new Size(240, 600), card.Size);

        // Act：模拟设备旋转时 DeviceSession.HandleSessionPacket（DeviceSession.cs:469）抛出的事件。
        RaiseResolutionChanged(session, 2400, 1080);

        // Assert：⭐ 修复前卡片纹丝不动（仍 240x600），横屏帧被压成上下大黑边。
        Assert.True(card.Width > card.Height,
                    $"旋转到横屏后卡片仍未翻转：{card.Width}x{card.Height}");

        ScreenView screen = FindScreen(card);
        Assert.Equal(2400, screen.VideoWidth);
        Assert.Equal(1080, screen.VideoHeight);

        Rectangle box = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, 2400, 1080);
        Assert.Equal(0, screen.Height - box.Height);
    }

    [Fact]
    public void 根因回归_横屏转回竖屏事件同样生效()
    {
        var info = new DeviceInfo("qa-rotate-back", DeviceState.Streaming, "QA")
        {
            VideoWidth = 2400,
            VideoHeight = 1080
        };
        DeviceCard card = NewCard(info, 1.0);
        using DeviceSession session = NewSession(info);
        card.Bind(session);

        Assert.True(card.Width > card.Height);

        RaiseResolutionChanged(session, 1080, 2400);

        Assert.Equal(new Size(240, 600), card.Size);
        Assert.Equal(1080, FindScreen(card).VideoWidth);
    }

    [Fact]
    public void 根因回归_旋转事件下发的目标尺寸等于运行态画面区()
    {
        // 目标尺寸必须等于真实画面区的 letterbox → 位图尺寸 == 绘制矩形，避免二次重采样（模糊）。
        var info = new DeviceInfo("qa-target", DeviceState.Streaming, "QA")
        {
            VideoWidth = 1080,
            VideoHeight = 2400
        };
        DeviceCard card = NewCard(info, 1.0);
        using DeviceSession session = NewSession(info);
        card.Bind(session);

        ScreenView screen = FindScreen(card);
        var reported = new List<Size>();
        screen.LetterboxChanged += (w, h) => reported.Add(new Size(w, h));

        RaiseResolutionChanged(session, 1920, 1080);

        Assert.NotEmpty(reported);
        Rectangle expected = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, 1920, 1080);
        Assert.Equal(new Size(expected.Width, expected.Height), reported[^1]);
    }

    [Fact]
    public void 根因回归_解绑后旋转事件不再改动卡片()
    {
        var info = new DeviceInfo("qa-unbind", DeviceState.Streaming, "QA")
        {
            VideoWidth = 1080,
            VideoHeight = 2400
        };
        DeviceCard card = NewCard(info, 1.0);
        using DeviceSession session = NewSession(info);

        // 保活订阅者：解绑后事件字段仍非空，能真实触发一次，从而证明「卡片确实不再响应」。
        session.ResolutionChanged += (_, _, _) => { };

        card.Bind(session);
        card.Unbind();

        Size before = card.Size;
        RaiseResolutionChanged(session, 2400, 1080);

        Assert.Equal(before, card.Size);
    }

    [Fact]
    public void 根因对照_修复前的固定竖长卡片会产生数百像素黑边()
    {
        // 对照组：修复前卡片恒为 240x600 → 运行态画面区 236x506，塞入 2400x1080 横屏帧。
        Rectangle before = CoordinateMapper.ComputeLetterbox(236, 506, 2400, 1080);
        Assert.True(506 - before.Height > 300, $"对照组黑边应当很大，实际 {506 - before.Height}px");

        // 实验组：真实卡片链路，上下黑边归零。
        DeviceCard card = NewCard(2400, 1080, 1.0);
        ScreenView screen = FindScreen(card);
        Rectangle after = CoordinateMapper.ComputeLetterbox(screen.Width, screen.Height, 2400, 1080);
        Assert.Equal(0, screen.Height - after.Height);
    }

    // ============================================================ 验收点 (d) 多设备网格

    [Fact]
    public void 验收D1_网格横竖混排不重叠且不超出行宽()
    {
        using var flow = new FlowLayoutPanel
        {
            Width = 1200,
            Height = 900,
            WrapContents = true,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight
        };

        (int W, int H)[] devices =
        {
            (1080, 2400),   // 竖
            (2400, 1080),   // 横
            (1920, 1080),   // 横
            (1080, 1920),   // 竖
            (2560, 1080),   // 横超宽
            (0, 0),         // 方向未知
        };

        var cards = new List<DeviceCard>();
        foreach ((int w, int h) in devices)
        {
            DeviceCard card = NewCard(w, h, 1.0);
            cards.Add(card);
            flow.Controls.Add(card);
        }

        flow.PerformLayout();

        for (int i = 0; i < cards.Count; i++)
        {
            DeviceCard card = cards[i];
            Size predicted = DeviceCardLayout.ComputeCardSize(_cfg.CardBaseWidth, _cfg.CardBaseHeight,
                                                              1.0, devices[i].W, devices[i].H);

            Assert.Equal(predicted, card.Size);
            Assert.True(card.Left >= 0, $"卡片 {i} 左边越界：{card.Left}");
            Assert.True(card.Right <= flow.Width, $"卡片 {i} 右边越界：{card.Right} > {flow.Width}");
        }

        for (int i = 0; i < cards.Count; i++)
        {
            for (int j = i + 1; j < cards.Count; j++)
            {
                Assert.False(cards[i].Bounds.IntersectsWith(cards[j].Bounds),
                             $"卡片 {i}({cards[i].Bounds}) 与 {j}({cards[j].Bounds}) 重叠");
            }
        }
    }

    [Fact]
    public void 验收D2_网格内单卡旋转不影响其它卡片()
    {
        using var flow = new FlowLayoutPanel
        {
            Width = 1200,
            Height = 900,
            WrapContents = true,
            AutoSize = false
        };

        DeviceCard a = NewCard(1080, 2400, 1.0);
        DeviceCard b = NewCard(1080, 2400, 1.0);
        DeviceCard c = NewCard(1080, 2400, 1.0);
        flow.Controls.AddRange(new Control[] { a, b, c });
        flow.PerformLayout();

        Size aBefore = a.Size;
        Size cBefore = c.Size;

        b.ApplyOrientation(2400, 1080);
        flow.PerformLayout();

        Assert.Equal(aBefore, a.Size);
        Assert.Equal(cBefore, c.Size);
        Assert.True(b.Width > b.Height);
        Assert.False(a.Bounds.IntersectsWith(b.Bounds));
        Assert.False(b.Bounds.IntersectsWith(c.Bounds));
    }

    [Fact]
    public void 验收D3_网格整体改缩放时横竖卡片同步跟随()
    {
        using var flow = new FlowLayoutPanel { Width = 1600, Height = 1200, WrapContents = true, AutoSize = false };

        DeviceCard portrait = NewCard(1080, 2400, 1.0);
        DeviceCard landscape = NewCard(2400, 1080, 1.0);
        flow.Controls.AddRange(new Control[] { portrait, landscape });
        flow.PerformLayout();

        foreach (double scale in Scales)
        {
            portrait.ApplyScale(scale);
            landscape.ApplyScale(scale);
            flow.PerformLayout();

            Assert.Equal(DeviceCardLayout.ComputeCardSize(_cfg.CardBaseWidth, _cfg.CardBaseHeight, scale, 1080, 2400),
                         portrait.Size);
            Assert.Equal(DeviceCardLayout.ComputeCardSize(_cfg.CardBaseWidth, _cfg.CardBaseHeight, scale, 2400, 1080),
                         landscape.Size);
            Assert.True(landscape.Width > landscape.Height, $"@{scale} 横屏卡片退化成竖长");
            Assert.True(portrait.Height > portrait.Width, $"@{scale} 竖屏卡片退化成横长");
            Assert.False(portrait.Bounds.IntersectsWith(landscape.Bounds));
        }
    }

    // ============================================================ 结构完整性：标题栏 / 按键区 / 下限

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 结构E1_横屏各档位标题栏与按键区高度不被压缩(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(videoW, videoH, scale);
            Label title = FindOne<Label>(card);
            ScreenView screen = FindScreen(card);

            Assert.Equal(DeviceCard.TitleHeight, title.Height);

            // 运行态：卡片高 = 边框(2) + 内边距(2) + 标题 + 画面区 + 按键区。
            int buttonArea = card.Height - 4 - title.Height - screen.Height;
            Assert.Equal(DeviceCard.ButtonAreaHeight, buttonArea);
        }
    }

    [Theory]
    [MemberData(nameof(LandscapeFrames))]
    public void 结构E2_横屏画面区从不被ScreenView下限顶起(int videoW, int videoH)
    {
        foreach (double scale in Scales)
        {
            DeviceCard card = NewCard(videoW, videoH, scale);
            ScreenView screen = FindScreen(card);

            Assert.True(screen.Width >= screen.MinimumSize.Width,
                        $"{videoW}x{videoH} @{scale}：画面区宽 {screen.Width} < 下限 {screen.MinimumSize.Width}");
            Assert.True(screen.Height >= screen.MinimumSize.Height,
                        $"{videoW}x{videoH} @{scale}：画面区高 {screen.Height} < 下限 {screen.MinimumSize.Height}");

            // 画面区必须严格落在卡片内，不能被 MinimumSize 撑出卡片再被裁切。
            Assert.True(screen.Width < card.Width && screen.Height < card.Height,
                        $"{videoW}x{videoH} @{scale}：画面区 {screen.Size} 溢出卡片 {card.Size}");
        }
    }

    [Fact]
    public void 结构E3_ScreenView下限必须低于所有档位的最小画面区()
    {
        int minW = int.MaxValue;
        int minH = int.MaxValue;

        foreach (object[] row in LandscapeFrames.Select(d => d.ToArray()))
        {
            var w = (int)row[0];
            var h = (int)row[1];
            foreach (double scale in Scales)
            {
                ScreenView screen = FindScreen(NewCard(w, h, scale));
                minW = Math.Min(minW, screen.Width);
                minH = Math.Min(minH, screen.Height);
            }
        }

        foreach (double scale in Scales)
        {
            ScreenView screen = FindScreen(NewCard(1080, 2400, scale));
            minW = Math.Min(minW, screen.Width);
            minH = Math.Min(minH, screen.Height);
        }

        var probe = new ScreenView();
        _owned.Add(probe);

        Assert.True(probe.MinimumSize.Width <= minW, $"ScreenView 宽下限 {probe.MinimumSize.Width} > 实测最小画面区宽 {minW}");
        Assert.True(probe.MinimumSize.Height <= minH, $"ScreenView 高下限 {probe.MinimumSize.Height} > 实测最小画面区高 {minH}");
    }

    // ============================================================ 边界与健壮性

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    public void 健壮F1_非法分辨率不改变已有尺寸(int videoW, int videoH)
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        Size before = card.Size;

        card.ApplyOrientation(videoW, videoH);

        Assert.Equal(before, card.Size);
    }

    [Fact]
    public void 健壮F2_重复下发同一分辨率是幂等的()
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        card.ApplyOrientation(1920, 1080);
        Size once = card.Size;

        card.ApplyOrientation(1920, 1080);
        card.ApplyOrientation(1920, 1080);

        Assert.Equal(once, card.Size);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void 健壮F3_非法缩放在真实卡片上回落到一倍(double scale)
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        card.ApplyScale(scale);

        Assert.Equal(new Size(240, 600), card.Size);
    }

    [Fact]
    public void 健壮F4_正方形画面按竖屏处理不触发翻转()
    {
        DeviceCard card = NewCard(1080, 2400, 1.0);
        Size before = card.Size;

        card.ApplyOrientation(1080, 1080);

        Assert.Equal(before, card.Size);
    }

    // ============================================================ 辅助

    /// <summary>建一张<b>已创建句柄</b>的卡片并登记待释放（视频尺寸 &lt;= 0 表示方向未知）。</summary>
    private DeviceCard NewCard(int videoW, int videoH, double scale)
    {
        var info = new DeviceInfo($"qa-{Guid.NewGuid():N}", DeviceState.Streaming, "QA")
        {
            VideoWidth = videoW,
            VideoHeight = videoH
        };

        DeviceCard card = NewCard(info, scale);
        return card;
    }

    /// <summary>建一张<b>已创建句柄</b>的卡片并登记待释放。</summary>
    private DeviceCard NewCard(DeviceInfo info, double scale)
    {
        var card = new DeviceCard(info, _manager);
        _owned.Add(card);

        // ⭐ 必须先建句柄：BorderStyle 只有在句柄存在时才缩减客户区，否则测到的是非运行态尺寸。
        _ = card.Handle;
        card.ApplyScale(scale);
        card.PerformLayout();
        return card;
    }

    /// <summary>建一个不启动任何线程 / socket 的会话。</summary>
    private DeviceSession NewSession(DeviceInfo info)
    {
        return new DeviceSession(info, new ScrcpyServerLauncher(new AdbClient(string.Empty), _cfg), _cfg);
    }

    /// <summary>取卡片内部真实的 <see cref="ScreenView"/>（走控件树，不用私有字段）。</summary>
    private static ScreenView FindScreen(DeviceCard card) => FindOne<ScreenView>(card);

    /// <summary>在控件树中找唯一的指定类型控件。</summary>
    private static T FindOne<T>(Control root) where T : Control
    {
        List<T> hits = Descendants(root).OfType<T>().ToList();
        Assert.Single(hits);
        return hits[0];
    }

    /// <summary>深度优先遍历控件树。</summary>
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control g in Descendants(child))
            {
                yield return g;
            }
        }
    }

    /// <summary>
    /// 触发 <see cref="DeviceSession.ResolutionChanged"/>（字段式事件的后备字段），
    /// 等价于设备旋转时 <c>HandleSessionPacket</c> 的抛出行为。
    /// </summary>
    private static void RaiseResolutionChanged(DeviceSession session, int videoW, int videoH)
    {
        FieldInfo? field = typeof(DeviceSession).GetField(nameof(DeviceSession.ResolutionChanged),
                                                          BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        session.Info.VideoWidth = videoW;
        session.Info.VideoHeight = videoH;

        var handler = (Action<string, int, int>?)field!.GetValue(session);
        Assert.NotNull(handler);
        handler!(session.Serial, videoW, videoH);
    }
}
