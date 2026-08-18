using System;
using System.Drawing;
using System.Drawing.Imaging;

using FFmpeg.AutoGen;

namespace MultiScrcpy.Core.Decoder;

/// <summary>
/// AVFrame（YUV420P）→ <see cref="Bitmap"/>（<see cref="PixelFormat.Format24bppRgb"/>）零拷贝转换器，
/// 实现架构文档 §6.4。
/// <para>
/// <b>关键事实</b>：GDI+ 的 <c>Format24bppRgb</c> 内存布局是 <b>B,G,R</b> 顺序（不是 RGB！），
/// 行步长 <c>Stride</c> 按 4 字节对齐。因此 <c>sws_scale</c> 的目标格式选
/// <c>AV_PIX_FMT_BGR24</c>，并把 <see cref="BitmapData.Scan0"/> / <see cref="BitmapData.Stride"/>
/// 直接当作 <c>dst[0]</c> / <c>dstStride[0]</c> 传入 —— 一次调用完成
/// 「色彩空间转换 + 降采样 + 写入位图」，零中间缓冲、零托管拷贝。
/// </para>
/// <para>
/// <b>⚠️ 该零拷贝写法只适用于「缩放」路径</b>（源尺寸 ≠ 目标尺寸，且源格式为 YUV420P）。
/// 一旦出现 <b>1:1 未缩放</b>（srcW==dstW &amp;&amp; srcH==dstH）的转换，swscale 会切到
/// unscaled SIMD 特化转换器，其单次写入可能越过行尾若干字节；而 GDI+ 位图缓冲恰好
/// <c>Stride*Height</c>、没有任何 SIMD 余量 → <b>原生越界写（0xC0000005）</b>，
/// 托管 <c>try/catch</c> 无法拦截，进程直接崩溃。
/// 因此截图路径（<see cref="ConvertToNewBitmap"/>）必须先写入 <c>av_image_alloc</c>
/// 申请的对齐缓冲，再逐行 memcpy 进位图。
/// </para>
/// <para><b>线程约束</b>：非线程安全，仅允许在解码线程上使用。</para>
/// </summary>
public sealed unsafe class FrameConverter : IDisposable
{
    /// <summary>目标尺寸量化粒度：避免用户拖窗时每像素都重建 SwsContext。</summary>
    public const int SizeAlignment = 16;

    /// <summary><c>av_image_alloc</c> 的对齐字节数：64 可覆盖 AVX-512，并留出 SIMD 写入余量。</summary>
    private const int NativeBufferAlignment = 64;

    /// <summary>BGR24 每像素字节数。</summary>
    private const int Bgr24BytesPerPixel = 3;

    private SwsContext* _sws;
    private int _dstW;
    private int _dstH;
    private int _flags;
    private bool _disposed;

    /// <summary>创建转换器。</summary>
    /// <param name="dstW">目标宽度（像素）。</param>
    /// <param name="dstH">目标高度（像素）。</param>
    /// <param name="swsFlags">缩放算法标志，默认 <c>SWS_BILINEAR</c>（画质/速度平衡）。</param>
    public FrameConverter(int dstW, int dstH, int swsFlags = ffmpeg.SWS_BILINEAR)
    {
        _dstW = Math.Max(1, dstW);
        _dstH = Math.Max(1, dstH);
        _flags = swsFlags <= 0 ? ffmpeg.SWS_BILINEAR : swsFlags;
    }

    /// <summary>当前目标宽度。</summary>
    public int DestinationWidth => _dstW;

    /// <summary>当前目标高度。</summary>
    public int DestinationHeight => _dstH;

    /// <summary>把任意尺寸量化到 <see cref="SizeAlignment"/> 的倍数（下限 16）。</summary>
    public static int Quantize(int value)
    {
        if (value <= SizeAlignment)
        {
            return SizeAlignment;
        }

        return (value + SizeAlignment - 1) & ~(SizeAlignment - 1);
    }

    /// <summary>
    /// 把解码帧缩放并转换后写入 <paramref name="bitmap"/>。
    /// </summary>
    /// <param name="framePtr"><c>AVFrame*</c> 指针。</param>
    /// <param name="bitmap">目标位图，尺寸必须等于 <see cref="DestinationWidth"/>×<see cref="DestinationHeight"/>，
    /// 像素格式必须为 <see cref="PixelFormat.Format24bppRgb"/>。</param>
    public void Convert(IntPtr framePtr, Bitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bitmap);

        if (framePtr == IntPtr.Zero)
        {
            throw new DecoderException("FrameConverter.Convert 收到空的 AVFrame 指针。");
        }

        if (bitmap.Width != _dstW || bitmap.Height != _dstH)
        {
            throw new DecoderException(
                $"目标位图尺寸不匹配：期望 {_dstW}x{_dstH}，实际 {bitmap.Width}x{bitmap.Height}。");
        }

        if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
        {
            throw new DecoderException($"目标位图像素格式必须是 Format24bppRgb，实际 {bitmap.PixelFormat}。");
        }

        AVFrame* frame = (AVFrame*)framePtr;
        if (frame->width <= 0 || frame->height <= 0)
        {
            throw new DecoderException($"解码帧尺寸非法：{frame->width}x{frame->height}。");
        }

        // 源参数变化（首帧 / 旋转）→ sws_getCachedContext 会自动复用或重建。
        _sws = ffmpeg.sws_getCachedContext(
            _sws,
            frame->width, frame->height, (AVPixelFormat)frame->format,
            _dstW, _dstH, AVPixelFormat.AV_PIX_FMT_BGR24,   // ⭐ BGR24 匹配 GDI+ 24bppRgb
            _flags, null, null, null);

        if (_sws == null)
        {
            throw new DecoderException("sws_getCachedContext 失败（无法创建缩放上下文）。");
        }

        var rect = new Rectangle(0, 0, _dstW, _dstH);
        BitmapData bd = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte_ptrArray4 dstData = default;
            int_array4 dstLine = default;
            dstData[0] = (byte*)bd.Scan0;
            dstLine[0] = bd.Stride;   // ⭐ 用 GDI+ 的 Stride，自动处理 4 字节对齐

            int scaled = ffmpeg.sws_scale(
                _sws,
                frame->data, frame->linesize,
                0, frame->height,
                dstData, dstLine);

            if (scaled <= 0)
            {
                throw new DecoderException($"sws_scale 失败，返回 {scaled}。");
            }
        }
        finally
        {
            bitmap.UnlockBits(bd);
        }
    }

    /// <summary>
    /// 截图专用：按<b>源分辨率</b>新建 <see cref="Bitmap"/>（PRD R-P1-4 要求保存设备原始分辨率）。
    /// <para>
    /// <b>⭐ 崩溃修复（0xC0000005 @ swscale-7.dll）</b>：本路径是 <b>1:1 未缩放</b>转换
    /// （dst 尺寸 == src 尺寸），swscale 会走 unscaled SIMD 特化转换器并可能越过行尾写入。
    /// 因此这里<b>绝不</b>把 GDI+ 位图的 <c>Scan0</c> 交给 <c>sws_scale</c>，而是：
    /// <c>av_image_alloc</c>（64 字节对齐 + SIMD 余量）→ <c>sws_scale</c> 写原生缓冲 →
    /// 逐行 <c>memcpy</c> 进位图（按各自 Stride 处理行宽差异）→ <c>av_freep</c> 释放。
    /// </para>
    /// <para>截图属低频操作，故用 <c>SWS_LANCZOS</c> 做色彩转换，质量优先。</para>
    /// </summary>
    /// <param name="framePtr"><c>AVFrame*</c> 指针。</param>
    /// <returns>调用方负责 <c>Dispose</c> 的新位图。</returns>
    public static Bitmap ConvertToNewBitmap(IntPtr framePtr)
    {
        if (framePtr == IntPtr.Zero)
        {
            throw new DecoderException("ConvertToNewBitmap 收到空的 AVFrame 指针。");
        }

        AVFrame* frame = (AVFrame*)framePtr;
        int w = frame->width;
        int h = frame->height;
        if (w <= 0 || h <= 0)
        {
            throw new DecoderException($"解码帧尺寸非法：{w}x{h}。");
        }

        byte_ptrArray4 dstData = default;
        int_array4 dstLine = default;

        int allocated = ffmpeg.av_image_alloc(
            ref dstData, ref dstLine, w, h, AVPixelFormat.AV_PIX_FMT_BGR24, NativeBufferAlignment);

        if (allocated < 0 || dstData[0] == null)
        {
            throw new DecoderException($"av_image_alloc 失败（{w}x{h} BGR24），返回 {allocated}。");
        }

        Bitmap? bmp = null;
        try
        {
            using (var tmp = new FrameConverter(w, h, ffmpeg.SWS_LANCZOS))
            {
                tmp.ScaleToNativeBuffer(frame, dstData, dstLine);
            }

            bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, w, h);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                int rowBytes = w * Bgr24BytesPerPixel;
                byte* src = dstData[0];
                var dst = (byte*)bd.Scan0;
                int srcStride = dstLine[0];
                int dstStride = bd.Stride;

                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(
                        src + (long)y * srcStride,
                        dst + (long)y * dstStride,
                        rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }

            Bitmap result = bmp;
            bmp = null;   // 交出所有权，避免下面的 catch 误释放
            return result;
        }
        catch
        {
            bmp?.Dispose();
            throw;
        }
        finally
        {
            byte* buffer = dstData[0];
            if (buffer != null)
            {
                ffmpeg.av_freep(&buffer);
                dstData[0] = null;
            }
        }
    }

    /// <summary>
    /// 把解码帧转换/缩放后写入<b>调用方提供的原生缓冲</b>（必须由 <c>av_image_alloc</c> 申请，
    /// 带对齐与 SIMD 余量）。<b>禁止传入 GDI+ 位图的 <c>Scan0</c></b>。
    /// </summary>
    /// <param name="frame">源 <c>AVFrame</c>。</param>
    /// <param name="dstData">目标平面指针数组（<c>dstData[0]</c> 为 BGR24 首地址）。</param>
    /// <param name="dstLine">目标行步长数组。</param>
    private void ScaleToNativeBuffer(AVFrame* frame, byte_ptrArray4 dstData, int_array4 dstLine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _sws = ffmpeg.sws_getCachedContext(
            _sws,
            frame->width, frame->height, (AVPixelFormat)frame->format,
            _dstW, _dstH, AVPixelFormat.AV_PIX_FMT_BGR24,
            _flags, null, null, null);

        if (_sws == null)
        {
            throw new DecoderException("sws_getCachedContext 失败（无法创建缩放上下文）。");
        }

        int scaled = ffmpeg.sws_scale(
            _sws,
            frame->data, frame->linesize,
            0, frame->height,
            dstData, dstLine);

        if (scaled <= 0)
        {
            throw new DecoderException($"sws_scale 失败，返回 {scaled}。");
        }
    }

    /// <summary>
    /// 调整目标尺寸；下次 <see cref="Convert"/> 时 <c>sws_getCachedContext</c> 自动重建上下文。
    /// </summary>
    public void Resize(int dstW, int dstH)
    {
        int w = Math.Max(1, dstW);
        int h = Math.Max(1, dstH);
        if (w == _dstW && h == _dstH)
        {
            return;
        }

        _dstW = w;
        _dstH = h;
    }

    /// <summary>切换缩放算法（例如 8 台同屏 CPU 吃紧时降为 <c>SWS_FAST_BILINEAR</c>）。</summary>
    public void SetFlags(int swsFlags)
    {
        int flags = swsFlags <= 0 ? ffmpeg.SWS_BILINEAR : swsFlags;
        if (flags == _flags)
        {
            return;
        }

        _flags = flags;
        ReleaseUnmanaged();   // 算法变化必须重建上下文
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseUnmanaged();
        GC.SuppressFinalize(this);
    }

    /// <summary>兜底释放 SwsContext。</summary>
    ~FrameConverter()
    {
        ReleaseUnmanaged();
    }

    private void ReleaseUnmanaged()
    {
        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
    }
}
