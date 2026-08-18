using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Core.Decoder;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// 「截图崩溃（0xC0000005）」+「100% 缩放画面模糊」两个缺陷的回归测试。
///
/// <para>
/// <b>缺陷 A — 点击截图整程序崩溃</b>：旧 <c>FrameConverter.ConvertToNewBitmap</c> 按源分辨率
/// 新建 GDI+ 位图，并把它的 <c>Scan0</c> 直接当作 <c>sws_scale</c> 的目标缓冲。该路径是
/// <b>1:1 未缩放</b>转换（dst 尺寸 == src 尺寸），swscale 会切到 unscaled SIMD 特化转换器，
/// 单次写入可能越过行尾若干字节；而 GDI+ 缓冲恰好 <c>Stride*Height</c>、没有任何 SIMD 余量
/// → <b>原生越界写</b>，托管 try/catch 拦不住，进程直接死。
/// 修复：<c>av_image_alloc</c>(align=64) 申请带余量的原生缓冲 → <c>sws_scale</c> 写原生缓冲
/// → 逐行 <c>Buffer.MemoryCopy</c> 进位图 → <c>av_freep</c> 释放。
/// </para>
///
/// <para>
/// <b>缺陷 B — 100% 缩放下画面模糊</b>：旧 <c>DeviceSession.SetTargetSize</c> 用
/// <c>FrameConverter.Quantize</c> 把宽高<b>各自</b>向上取整到 16 的倍数（如 211x466 → 224x480），
/// 导致渲染位图尺寸 ≠ <c>ScreenView</c> 的绘制矩形，GDI+ <c>DrawImage</c> 被迫做第二次重采样；
/// 且宽高独立量化还破坏了长宽比。修复：写入精确值，使「位图尺寸 == 绘制矩形」，走 1:1 拷贝。
/// </para>
///
/// <para>
/// <b>为什么用「源码结构守护」而不是真的调一次转换</b>：本测试工程刻意不加载任何 FFmpeg
/// 原生 DLL（见 csproj 注释），而复现缺陷 A 必须构造真实 <c>AVFrame</c> 并触发原生越界写——
/// 那本身就会让测试进程崩溃、无法断言。因此这里锁定「不把 <c>Scan0</c> 交给 <c>sws_scale</c>」
/// 这一<b>内存安全契约</b>；真实像素正确性仍需真机端到端验证。
/// </para>
/// </summary>
public class RenderQualityRegressionTests
{
    // ================================================================
    // 缺陷 B-1：SetTargetSize 必须写入精确尺寸（不再量化到 16 的倍数）
    // ================================================================

    [Theory]
    [InlineData(211, 466)]   // 典型 9:20 卡片画面区，两个维度都不是 16 的倍数
    [InlineData(300, 560)]   // 卡片基准尺寸
    [InlineData(1, 1)]       // 下边界
    [InlineData(1079, 2339)] // 接近 1080p，且刻意避开 16 对齐
    public void SetTargetSize必须写入精确尺寸而不量化(int width, int height)
    {
        using DeviceSession session = CreateSession();

        session.SetTargetSize(width, height);

        Assert.Equal(width, ReadPrivateInt(session, "_targetW"));
        Assert.Equal(height, ReadPrivateInt(session, "_targetH"));
    }

    /// <summary>
    /// 护栏用例：证明上面的断言<b>确实能抓到</b>量化回退——
    /// 若有人把 <c>Quantize</c> 加回 <c>SetTargetSize</c>，211x466 会变成 224x480。
    /// </summary>
    [Fact]
    public void SetTargetSize量化回退可被检出()
    {
        // 前提：Quantize 本身仍是「向上对齐到 16」，否则这个护栏就失去意义。
        Assert.Equal(224, FrameConverter.Quantize(211));
        Assert.Equal(480, FrameConverter.Quantize(466));

        using DeviceSession session = CreateSession();
        session.SetTargetSize(211, 466);

        Assert.NotEqual(FrameConverter.Quantize(211), ReadPrivateInt(session, "_targetW"));
        Assert.NotEqual(FrameConverter.Quantize(466), ReadPrivateInt(session, "_targetH"));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 720)]
    [InlineData(-5, -5)]
    public void SetTargetSize非法尺寸必须被忽略(int width, int height)
    {
        using DeviceSession session = CreateSession();

        session.SetTargetSize(360, 720);
        session.SetTargetSize(width, height);

        // 非法入参不得污染已有的合法目标尺寸。
        Assert.Equal(360, ReadPrivateInt(session, "_targetW"));
        Assert.Equal(720, ReadPrivateInt(session, "_targetH"));
    }

    [Fact]
    public void SetTargetSize源码中不得再调用Quantize()
    {
        string? source = TryReadSource("MultiScrcpyPanel", "Core", "DeviceSession.cs");
        if (source == null)
        {
            return;   // 源码树不可用（仅有编译产物）时跳过；行为已由上面的用例覆盖。
        }

        string body = StripComments(ExtractBetween(
            source,
            "public void SetTargetSize(int width, int height)",
            "public void RequestScreenshot("));

        Assert.DoesNotContain("Quantize", body, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _targetW, width)", body, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _targetH, height)", body, StringComparison.Ordinal);
    }

    // ================================================================
    // 缺陷 B-2：默认缩放算法必须是 SWS_BICUBIC(4)，不是 SWS_BILINEAR(2)
    // ================================================================

    [Fact]
    public void AppConfig默认缩放算法必须是BICUBIC()
    {
        // 4 == SWS_BICUBIC。降采样（设备 1080p → 卡片 ~300px）下 BILINEAR 细节丢失明显。
        Assert.Equal(4, new AppConfig().SwsFlags);
    }

    /// <summary>
    /// 用户<b>显式</b>写进配置文件的 <c>SwsFlags</c> 必须能存活一次 Save→Load 往返。
    ///
    /// <para>
    /// <b>这条用例锁的是一个设计决策，不只是当前实现</b>：默认值从 2 改成 4 之后，
    /// 有人会想给存量配置加「读到 2 就自动升成 4」的迁移。那种<b>值嗅探式</b>迁移会在
    /// <b>每次 Load 时都执行</b>，后果是：用户因 8 台同屏 CPU 吃紧而主动把 SwsFlags 调回 2
    /// （这正是 <c>AppConfig.SwsFlags</c> 注释里建议他们做的事），下次启动又被静默改回 4 ——
    /// 这个设置项将<b>永远设不回 2</b>。那是把「默认值陈旧」的小问题换成「配置存不住」的真 bug。
    /// </para>
    ///
    /// <para>
    /// 本用例对迁移方案是<b>中立</b>的：不做迁移 → 通过；用 <c>ConfigVersion</c> 版本号做
    /// 一次性迁移 → 迁移后版本已是新版、不再触发，仍然通过；只有<b>值嗅探式</b>迁移会让它变红。
    /// 所以它红了不代表实现写错，而代表有人正在引入「用户配置存不住」的行为，需要显式决策。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1)]   // SWS_FAST_BILINEAR：CPU 最省
    [InlineData(2)]   // SWS_BILINEAR：本次修复要替换掉的旧默认值 ← 最关键的一条
    [InlineData(4)]   // SWS_BICUBIC：新默认值
    public void 用户显式设置的SwsFlags必须能往返持久化(int userChoice)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MultiScrcpyCfgTest_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "settings.json");
        try
        {
            new AppConfig { SwsFlags = userChoice }.Save(file);

            Assert.Equal(userChoice, AppConfig.Load(file).SwsFlags);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不应影响断言结果。
            }
        }
    }

    // ================================================================
    // 缺陷 A：截图路径绝不能把 GDI+ 位图 Scan0 交给 sws_scale
    // ================================================================

    [Fact]
    public void 截图路径不得把位图Scan0交给swsscale()
    {
        string? source = TryReadFrameConverterSource();
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "public static Bitmap ConvertToNewBitmap(IntPtr framePtr)",
            "private void ScaleToNativeBuffer("));

        // 核心不变量：本方法自己不得调用 sws_scale——必须委托给 ScaleToNativeBuffer，
        // 由后者写入 av_image_alloc 申请的对齐缓冲。
        Assert.DoesNotContain("sws_scale", body, StringComparison.Ordinal);

        // 且绝不能把位图缓冲当作 sws 目标平面。
        Assert.DoesNotContain("dstData[0] = (byte*)bd.Scan0", body, StringComparison.Ordinal);

        // 必备的三段式：申请对齐原生缓冲 → 转码 → 逐行拷回位图 → 释放。
        Assert.Contains("av_image_alloc", body, StringComparison.Ordinal);
        Assert.Contains("ScaleToNativeBuffer", body, StringComparison.Ordinal);
        Assert.Contains("Buffer.MemoryCopy", body, StringComparison.Ordinal);
        Assert.Contains("av_freep", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleToNativeBuffer不得感知GDI位图()
    {
        string? source = TryReadFrameConverterSource();
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "private void ScaleToNativeBuffer(",
            "public void Resize(int dstW, int dstH)"));

        // 该方法只接受调用方提供的原生缓冲，出现 Scan0/LockBits 即说明契约被破坏。
        Assert.DoesNotContain("Scan0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LockBits", body, StringComparison.Ordinal);
        Assert.Contains("sws_scale", body, StringComparison.Ordinal);
    }

    [Fact]
    public void av_image_alloc必须使用64字节对齐()
    {
        string? source = TryReadFrameConverterSource();
        if (source == null)
        {
            return;
        }

        // 对齐值同时提供 SIMD 写入余量（av_image_alloc 内部额外多分配 align 字节），
        // 是缺陷 A 修复能成立的前提。
        FieldInfo? f = typeof(FrameConverter).GetField(
            "NativeBufferAlignment",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(f);
        Assert.Equal(64, (int)f!.GetRawConstantValue()!);

        string body = StripComments(ExtractBetween(
            source,
            "public static Bitmap ConvertToNewBitmap(IntPtr framePtr)",
            "private void ScaleToNativeBuffer("));

        Assert.Contains("NativeBufferAlignment", body, StringComparison.Ordinal);
    }

    /// <summary>渲染主循环的零拷贝写法必须保留（缩放路径用 Scan0 是安全且必要的）。</summary>
    [Fact]
    public void 渲染主循环必须保持零拷贝()
    {
        string? source = TryReadFrameConverterSource();
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "public void Convert(IntPtr framePtr, Bitmap bitmap)",
            "public static Bitmap ConvertToNewBitmap("));

        Assert.Contains("dstData[0] = (byte*)bd.Scan0", body, StringComparison.Ordinal);
        Assert.Contains("sws_scale", body, StringComparison.Ordinal);
    }

    // ================================================================
    // 缺陷 B-3：ScreenView 绘制与 letterbox 下发的结构守护
    // ================================================================

    [Fact]
    public void OnPaint的1比1分支必须在DrawImage后立即返回()
    {
        string? source = TryReadSource("MultiScrcpyPanel", "UI", "ScreenView.cs");
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "protected override void OnPaint(PaintEventArgs e)",
            "protected override void OnResize(EventArgs e)"));

        int copyIdx = body.IndexOf("CompositingMode.SourceCopy", StringComparison.Ordinal);
        Assert.True(copyIdx >= 0, "OnPaint 未找到 1:1 分支的 CompositingMode.SourceCopy。");

        int drawIdx = body.IndexOf("DrawImage", copyIdx, StringComparison.Ordinal);
        Assert.True(drawIdx >= 0, "SourceCopy 之后未找到 DrawImage。");

        int returnIdx = body.IndexOf("return;", drawIdx, StringComparison.Ordinal);
        Assert.True(returnIdx >= 0,
            "1:1 分支 DrawImage 之后必须立即 return——否则会在 CompositingMode.SourceCopy " +
            "状态下继续绘制（文字/占位符），SourceCopy 不做 alpha 混合会画出黑底方块。");

        int nextBranchIdx = body.IndexOf("InterpolationMode.HighQuality", drawIdx, StringComparison.Ordinal);
        if (nextBranchIdx >= 0)
        {
            Assert.True(returnIdx < nextBranchIdx,
                "1:1 分支必须在进入高质量插值分支之前 return。");
        }
    }

    [Fact]
    public void 分辨率未知时CurrentLetterbox必须返回Empty()
    {
        string? source = TryReadSource("MultiScrcpyPanel", "UI", "ScreenView.cs");
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "public Rectangle CurrentLetterbox()",
            "protected override void OnPaint(PaintEventArgs e)"));

        // 旧实现在分辨率未知时返回整个控件矩形，宿主据此下发长宽比错误的目标尺寸 → 首帧模糊。
        Assert.Contains("_videoW <= 0", body, StringComparison.Ordinal);
        Assert.Contains("_videoH <= 0", body, StringComparison.Ordinal);
        Assert.Contains("return Rectangle.Empty;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void 空letterbox不得下发给宿主()
    {
        string? source = TryReadSource("MultiScrcpyPanel", "UI", "ScreenView.cs");
        if (source == null)
        {
            return;
        }

        string body = StripComments(ExtractBetween(
            source,
            "private void RaiseLetterboxChanged()",
            "\n}"));

        int guardIdx = body.IndexOf("r.Width <= 0", StringComparison.Ordinal);
        int invokeIdx = body.IndexOf("LetterboxChanged?.Invoke", StringComparison.Ordinal);

        Assert.True(guardIdx >= 0, "RaiseLetterboxChanged 缺少 r.Width <= 0 守卫。");
        Assert.True(invokeIdx >= 0, "RaiseLetterboxChanged 未找到事件下发。");
        Assert.True(guardIdx < invokeIdx, "守卫必须在事件下发之前。");
    }

    // ================================================================
    // helpers
    // ================================================================

    /// <summary>
    /// 构造一个不启动任何线程、不接触 adb、不加载 FFmpeg 的 <see cref="DeviceSession"/>。
    /// </summary>
    private static DeviceSession CreateSession()
    {
        var cfg = new AppConfig { AdbPath = "adb-not-used-in-this-test" };
        var launcher = new ScrcpyServerLauncher(new AdbClient(cfg.AdbPath), cfg);
        return new DeviceSession(new DeviceInfo("TEST-SERIAL"), launcher, cfg);
    }

    private static int ReadPrivateInt(DeviceSession session, string fieldName)
    {
        FieldInfo f = typeof(DeviceSession).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"未找到 DeviceSession.{fieldName} 字段——目标尺寸的存储方式可能已变更。");

        return (int)f.GetValue(session)!;
    }

    private static string? TryReadFrameConverterSource()
        => TryReadSource("MultiScrcpyPanel", "Core", "Decoder", "FrameConverter.cs");

    /// <summary>
    /// 通过编译期记录的本文件路径定位仓库内源码；找不到时返回 <c>null</c>。
    /// </summary>
    private static string? TryReadSource(params string[] relativeParts)
        => TryReadSourceCore(relativeParts);

    private static string? TryReadSourceCore(string[] relativeParts, [CallerFilePath] string thisFile = "")
    {
        try
        {
            // <repo>/MultiScrcpy.Tests/Core/RenderQualityRegressionTests.cs → <repo>
            string? coreDir = Path.GetDirectoryName(thisFile);
            string? testsDir = coreDir == null ? null : Path.GetDirectoryName(coreDir);
            string? repoRoot = testsDir == null ? null : Path.GetDirectoryName(testsDir);
            if (repoRoot == null)
            {
                return null;
            }

            string path = Path.Combine(repoRoot, Path.Combine(relativeParts));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>截取 <paramref name="startMarker"/>（含）到 <paramref name="endMarker"/>（不含）之间的源码。</summary>
    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未在源码中找到锚点：{startMarker}");

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.Length;
        }

        return source[start..end];
    }

    /// <summary>去掉 <c>//</c> 行注释与 <c>/* */</c> 块注释，避免注释里的关键词干扰结构断言。</summary>
    private static string StripComments(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }

                sb.Append('\n');
                continue;
            }

            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            sb.Append(code[i]);
        }

        return sb.ToString();
    }
}
