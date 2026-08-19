using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

using MultiScrcpy.Core.Scripting;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// OpenCvTemplateMatcher 合成图集成测试（对齐 D:\新建文件夹\OcrViewer 的 Vision.Match）。
/// <para>
/// 用 GDI+ 绘制带已知图标的帧，验证：
/// 1) 灰度 + Alpha mask 匹配能正确排除透明背景（mask 路径生效）；
/// 2) 0.85–1.15 多尺度在等尺寸、模板较小（上采样 ≈1.09×）、模板较大（下采样 ≈0.86×）三种情况下都能命中；
/// 3) 不同前景/背景的模板不会被误命中（返回 null）；
/// 4) 1:1 高分辨率模板匹配置信度接近 1。
/// 模板均为"带背景边距的图标"（TM_CCOEFF_NORMED 对纯色常数图会退化，真实模板不会如此）。
/// OpenCvSharp 原生运行时不可用时本测试优雅跳过，保证纯单测环境仍可通过。
/// </para>
/// </summary>
public class OpenCvMatcherTests
{
    private const int Fw = 400;
    private const int Fh = 300;
    private static readonly Color Bg = Color.LightGray;
    private static readonly Color Red = Color.Red;
    private static readonly Color Green = Color.Green;

    /// <summary>生成一个 size×size 的模板图：背景 + 在 (ix,iy) 处绘制 iconSize 的纯色方块。</summary>
    private static Bitmap MakeIcon(int size, int iconSize, int ix, int iy, Color icon)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Bg);
            g.FillRectangle(new SolidBrush(icon), ix, iy, iconSize, iconSize);
        }

        return bmp;
    }

    /// <summary>生成帧：背景 + 在 (fx,fy) 处绘制 iconSize 的红色方块。</summary>
    private static Bitmap MakeFrame(int fx, int fy, int iconSize)
    {
        var bmp = new Bitmap(Fw, Fh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Bg);
            g.FillRectangle(new SolidBrush(Red), fx, fy, iconSize, iconSize);
        }

        return bmp;
    }

    /// <summary>生成带棋盘格纹理的帧：避免纯色背景导致 masked CCoeffNormed 退化出现 Inf。</summary>
    private static Bitmap MakeCheckerFrame(int fx, int fy, int iconSize)
    {
        var bmp = new Bitmap(Fw, Fh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Bg);
            using var dark = new SolidBrush(Color.FromArgb(120, 120, 120));
            for (int y = 0; y < Fh; y += 30)
            {
                for (int x = 0; x < Fw; x += 30)
                {
                    if (((x / 30) + (y / 30)) % 2 == 0)
                    {
                        g.FillRectangle(dark, x, y, 30, 30);
                    }
                }
            }

            g.FillRectangle(new SolidBrush(Red), fx, fy, iconSize, iconSize);
        }

        return bmp;
    }

    /// <summary>生成透明背景模板：四周透明，仅在 (ix,iy) 处绘制 iconSize 的纯色方块。</summary>
    private static Bitmap MakeTransparentIcon(int size, int iconSize, int ix, int iy, Color icon)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(new SolidBrush(icon), ix, iy, iconSize, iconSize);
        }

        return bmp;
    }

    [Fact]
    public void 匹配_等尺寸图标_命中正确位置()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return; // OpenCvSharp 不可用则跳过

        using var frame = MakeFrame(100, 80, 48);
        using var tpl = MakeIcon(80, 48, 16, 16, Red);

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        double expectNx = (100 + 24.0) / Fw;
        double expectNy = (80 + 24.0) / Fh;
        Assert.InRange(m!.Nx, expectNx - 0.02, expectNx + 0.02);
        Assert.InRange(m.Ny, expectNy - 0.02, expectNy + 0.02);
        Assert.True(m.Score >= 0.8, $"相似度应较高，实际 {m.Score:F3}");
    }

    [Fact]
    public void 匹配_模板比图标小_上采样命中()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return;

        // 帧中图标 48×48；模板 72×72 内含 44×44 红方块（边距 14）。
        // 多尺度在 s=1.09（48/44）时模板方块放大到 ~48，命中（落在 0.85–1.15 范围内）。
        using var frame = MakeFrame(100, 80, 48);
        using var tpl = MakeIcon(72, 44, 14, 14, Red);

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        double expectNx = (100 + 24.0) / Fw;
        double expectNy = (80 + 24.0) / Fh;
        Assert.InRange(m!.Nx, expectNx - 0.03, expectNx + 0.03);
        Assert.InRange(m.Ny, expectNy - 0.03, expectNy + 0.03);
        Assert.True(m.Score >= 0.7, $"相似度应较高，实际 {m.Score:F3}");
    }

    [Fact]
    public void 匹配_模板比图标大_下采样命中()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return;

        // 帧中图标 48×48；模板 96×96 内含 56×56 红方块（边距 20）。
        // 多尺度在 s=0.86（48/56）时模板方块缩小到 ~48，命中（落在 0.85–1.15 范围内）。
        using var frame = MakeFrame(150, 100, 48);
        using var tpl = MakeIcon(96, 56, 20, 20, Red);

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        double expectNx = (150 + 24.0) / Fw;
        double expectNy = (100 + 24.0) / Fh;
        Assert.InRange(m!.Nx, expectNx - 0.03, expectNx + 0.03);
        Assert.InRange(m.Ny, expectNy - 0.03, expectNy + 0.03);
        Assert.True(m.Score >= 0.7, $"相似度应较高，实际 {m.Score:F3}");
    }

    [Fact]
    public void 匹配_透明背景模板_AlphaMask生效并命中()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return;

        // 帧背景使用棋盘格（避免纯色背景导致 masked CCoeffNormed 出现 Inf 的退化情形），
        // 图标为红色方块并带蓝色小标记；模板四周透明，只保留相同图标。
        // 若 alpha mask 未生效，透明区域会被当作黑色背景参与匹配，与灰色帧背景冲突导致相似度骤降。
        // 该用例即验证 OpenCvSharp 的 mask 路径确实排除了透明背景。
        using var frame = MakeCheckerFrame(120, 90, 48);
        using (var g = Graphics.FromImage(frame))
            g.FillRectangle(new SolidBrush(Color.Blue), 132, 102, 12, 12);
        using var tpl = MakeTransparentIcon(96, 48, 24, 24, Red);
        using (var g = Graphics.FromImage(tpl))
            g.FillRectangle(new SolidBrush(Color.Blue), 36, 36, 12, 12);

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        double expectNx = (120 + 24.0) / Fw;
        double expectNy = (90 + 24.0) / Fh;
        Assert.InRange(m!.Nx, expectNx - 0.02, expectNx + 0.02);
        Assert.InRange(m.Ny, expectNy - 0.02, expectNy + 0.02);
        Assert.True(m.Score >= 0.85, $"透明模板（mask 生效）相似度应接近 1，实际 {m.Score:F3}");
    }

    [Fact]
    public void 匹配_帧中不存在模板_返回null()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return;

        // 帧中是红色方块（浅灰背景）；模板是绿色方块置于黑色背景上，
        // 前景与背景均不匹配（灰度下帧为"暗方块亮底"、模板为"亮方块暗底"，相位相反），
        // CCOEFF_NORMED 应为负/近 0，确保不会被误命中。
        using var frame = MakeFrame(100, 80, 48);
        using var tpl = new Bitmap(64, 64, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(tpl))
        {
            g.Clear(Color.Black);
            g.FillRectangle(new SolidBrush(Green), 16, 16, 32, 32);
        }

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.Null(m);
    }

    [Fact]
    public void 匹配_高分辨率11模板_置信度接近1()
    {
        if (!OpenCvTemplateMatcher.IsAvailable) return;

        // 模拟 OcrViewer 场景：视频流已是高分辨率，模板与目标 1:1。
        // 此时只需在 0.85–1.15 范围内搜索，scale 1.00 即可给出接近 1.0 的置信度。
        const int iconSize = 91;
        using var frame = new Bitmap(900, 700, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Bg);
            g.FillRectangle(new SolidBrush(Red), 564, 16, iconSize, iconSize);
        }

        // 模板带背景边距：避免整图都是纯红色导致 TM_CCOEFF_NORMED 退化。
        const int tplSize = 120;
        using var tpl = new Bitmap(tplSize, tplSize, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(tpl))
        {
            g.Clear(Bg);
            g.FillRectangle(new SolidBrush(Red), (tplSize - iconSize) / 2, (tplSize - iconSize) / 2, iconSize, iconSize);
        }

        TemplateMatch? m = new OpenCvTemplateMatcher().Match(frame, tpl, 0.15);
        Assert.NotNull(m);
        double expectNx = (564 + iconSize / 2.0) / 900;
        double expectNy = (16 + iconSize / 2.0) / 700;
        Assert.InRange(m!.Nx, expectNx - 0.01, expectNx + 0.01);
        Assert.InRange(m.Ny, expectNy - 0.01, expectNy + 0.01);
        Assert.True(m.Score >= 0.95, $"1:1 高分辨率匹配置信度应接近 1，实际 {m.Score:F3}");
    }

    [Fact]
    public void 匹配_OcrViewer参考图对_高置信度命中_金标准()
    {
        // 金标准：照搬 D:\新建文件夹\OcrViewer 的 Vision.Match 机制后，
        // 参考图对 1.jpg(场景) / 2.png(模板) 必须高置信度命中（Score >= 0.90）。
        // 该用例直接验证"照搬 OcrViewer 机制"是否达标——参考图对若不能高置信命中即视为回归。
        const string scenePath = @"D:\新建文件夹\1.jpg";
        const string templPath = @"D:\新建文件夹\2.png";
        if (!OpenCvTemplateMatcher.IsAvailable) return;                    // OpenCvSharp 原生运行时缺失则跳过
        if (!File.Exists(scenePath) || !File.Exists(templPath)) return;     // 参考图缺失则跳过，避免 CI 失败

        using var scene = new Bitmap(scenePath);
        using var tpl = new Bitmap(templPath);

        // maxError 取宽松值（0.5），确保只要能命中就返回结果，由下方断言卡 0.90 金标准阈值。
        TemplateMatch? m = new OpenCvTemplateMatcher().Match(scene, tpl, 0.5);
        Assert.NotNull(m); // 参考图对必须能命中，否则视为源码回归
        Assert.True(
            m!.Score >= 0.90,
            $"OcrViewer 参考图对相似度应 >= 0.90（金标准），实际 {m.Score:F4}（loc=({m.Nx:F3},{m.Ny:F3}) size=({m.HalfW:F3},{m.HalfH:F3})）");
    }
}
