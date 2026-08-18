using System;
using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core.Decoder;

/// <summary>
/// 基于 FFmpeg（libavcodec）的软解码器，实现架构文档 §6.2 / §6.3。
/// <para>
/// 关键设计（逐条对应 §6.3 要点清单）：
/// ① config packet（SPS/PPS）缓存后与下一个 IDR 前置合并，绝不单独送解码器、绝不丢弃；
/// ② 包尾补 <c>AV_INPUT_BUFFER_PADDING_SIZE</c>(64) 字节零填充（FFmpeg 硬性 API 契约）；
/// ③ 关键帧门禁：首个关键帧到达前丢弃 P/B 帧；
/// ④ 单帧失败 = WARN + 丢帧；连续 30 帧失败 = ERROR + 重建解码器；
/// ⑤ 不使用 <c>av_parser_parse2</c>（scrcpy v4.0 的 media packet 已帧对齐，parser 会额外引入约 33ms 延迟）。
/// </para>
/// <para>
/// <b>线程约束</b>：本类<b>非线程安全</b>，只允许在单一的流读取线程上调用
/// <see cref="Open"/> / <see cref="TryDecode"/> / <see cref="Reset"/>；
/// <see cref="Dispose"/> 必须在该线程已 Join 之后调用。
/// </para>
/// </summary>
public sealed unsafe class H264Decoder : IVideoDecoder
{
    /// <summary>连续解码失败达到该阈值即重建解码上下文（对齐 Python 版 §8.3）。</summary>
    public const int MaxConsecutiveFailures = 30;

    private AVCodecContext* _ctx;
    private AVPacket* _pkt;
    private AVFrame* _frame;

    /// <summary>喂流用的托管缓冲，复用以避免每帧 GC。</summary>
    private byte[] _inputBuf = Array.Empty<byte>();

    private byte[]? _pendingConfig;
    private bool _gotKeyFrame;
    private int _consecutiveFailures;
    private uint _codecId;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsOpen => _ctx != null;

    /// <summary>当前打开的 codec id（未打开时为 0）。</summary>
    public uint CodecId => _codecId;

    /// <summary>累计成功解出的帧数（供日志 / 自检使用）。</summary>
    public long DecodedFrameCount { get; private set; }

    /// <inheritdoc />
    public void Open(uint codecId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ctx != null)
        {
            Reset();
        }

        AVCodecID id = codecId switch
        {
            ScrcpyConstants.CODEC_H264 => AVCodecID.AV_CODEC_ID_H264,
            ScrcpyConstants.CODEC_H265 => AVCodecID.AV_CODEC_ID_HEVC,
            ScrcpyConstants.CODEC_AV1 => AVCodecID.AV_CODEC_ID_AV1,
            _ => throw new DecoderException($"不支持的 codec id: 0x{codecId:X8}")
        };

        AVCodec* codec = ffmpeg.avcodec_find_decoder(id);
        if (codec == null)
        {
            throw new DecoderException($"未找到解码器 {id}（请确认 FFmpeg 构建包含该解码器）。");
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
        {
            throw new DecoderException("avcodec_alloc_context3 失败（内存不足）。");
        }

        // ⭐ 低延迟三件套：投屏场景绝不能有帧重排延迟。
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;   // 禁用 FRAME 多线程（会引入 thread_count-1 帧延迟）
        _ctx->thread_count = 2;

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            ReleaseUnmanaged();
            throw new DecoderException($"avcodec_open2 失败: {FFmpegError(ret)}");
        }

        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_pkt == null || _frame == null)
        {
            ReleaseUnmanaged();
            throw new DecoderException("av_packet_alloc / av_frame_alloc 失败（内存不足）。");
        }

        _codecId = codecId;
        _pendingConfig = null;
        _gotKeyFrame = false;
        _consecutiveFailures = 0;

        Log.Info($"解码器已就绪：{ScrcpyConstants.CodecName(codecId)}（low_delay + slice 多线程 x2）。");
    }

    /// <inheritdoc />
    public void TryDecode(in MediaPacket packet, Action<IntPtr> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_disposed)
        {
            return;
        }

        if (_ctx == null || _pkt == null || _frame == null)
        {
            throw new DecoderException("解码器尚未 Open()，无法解码。");
        }

        byte[] data = packet.Data ?? Array.Empty<byte>();

        // ① config packet（SPS/PPS）：缓存，不单独送解码器。
        if (packet.IsConfig)
        {
            _pendingConfig = data.Length > 0 ? data : null;
            Log.Debug($"缓存 config packet，{data.Length} 字节。");
            return;
        }

        // ② 关键帧门禁：解码器尚未拿到过任何关键帧前，丢弃 P/B 帧。
        if (!_gotKeyFrame)
        {
            if (!packet.IsKeyFrame)
            {
                return;
            }

            _gotKeyFrame = true;
        }

        // ③ 前置合并 config：SPS/PPS 与 IDR 拼成一个 AU 送入。
        int cfgLen = _pendingConfig?.Length ?? 0;
        int total = cfgLen + data.Length;
        if (total <= 0)
        {
            return;
        }

        // ④ ⚠️ FFmpeg 要求包尾有 AV_INPUT_BUFFER_PADDING_SIZE(64) 字节零填充，
        //    否则 h264 位流读取器会越界读到脏数据（表现为偶发花屏 / 崩溃）。
        EnsureInputBuffer(total + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        if (cfgLen > 0)
        {
            _pendingConfig!.CopyTo(_inputBuf, 0);
        }

        data.CopyTo(_inputBuf, cfgLen);
        Array.Clear(_inputBuf, total, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
        _pendingConfig = null;

        int sendRet;
        fixed (byte* p = _inputBuf)
        {
            _pkt->data = p;
            _pkt->size = total;
            _pkt->pts = packet.Pts;
            _pkt->dts = ffmpeg.AV_NOPTS_VALUE;
            _pkt->flags = packet.IsKeyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;

            // 非引用计数包（_pkt->buf == null）：avcodec_send_packet 内部会拷贝，
            // 因此在 fixed 块内调用即安全（§6.3 要点 5）。
            sendRet = ffmpeg.avcodec_send_packet(_ctx, _pkt);

            // 立刻解除悬垂指针，防止后续误用已解除固定的托管内存。
            _pkt->data = null;
            _pkt->size = 0;
        }

        if (sendRet < 0)
        {
            HandleSendFailure(sendRet);
            return;
        }

        _consecutiveFailures = 0;
        DrainFrames(onFrame);
    }

    /// <inheritdoc />
    public void Reset()
    {
        ReleaseUnmanaged();
        _pendingConfig = null;
        _gotKeyFrame = false;
        _consecutiveFailures = 0;
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
        _pendingConfig = null;
        _inputBuf = Array.Empty<byte>();
        GC.SuppressFinalize(this);
    }

    /// <summary>兜底释放非托管资源，避免 Dispose 漏调导致 FFmpeg 上下文泄漏。</summary>
    ~H264Decoder()
    {
        ReleaseUnmanaged();
    }

    /// <summary>把连续解码失败计数推进一格；达到阈值则重建解码上下文。</summary>
    private void HandleSendFailure(int ret)
    {
        if (++_consecutiveFailures >= MaxConsecutiveFailures)
        {
            Log.Error($"连续 {_consecutiveFailures} 帧解码失败（{FFmpegError(ret)}），重建解码器。");
            uint codecId = _codecId;
            try
            {
                Reset();
                Open(codecId);
            }
            catch (DecoderException ex)
            {
                Log.Error("重建解码器失败。", ex);
            }

            return;
        }

        Log.Warn($"avcodec_send_packet 失败: {FFmpegError(ret)}（连续第 {_consecutiveFailures} 次）。");
    }

    /// <summary>把解码器内已就绪的帧全部取出并同步回调消费。</summary>
    private void DrainFrames(Action<IntPtr> onFrame)
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (ret < 0)
            {
                Log.Warn($"avcodec_receive_frame 失败: {FFmpegError(ret)}");
                break;
            }

            try
            {
                DecodedFrameCount++;
                onFrame((IntPtr)_frame);   // 同步消费；回调返回后帧会被复用
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>按需扩容喂流缓冲（只在不足时分配，避免每帧 GC）。</summary>
    private void EnsureInputBuffer(int required)
    {
        if (_inputBuf.Length >= required)
        {
            return;
        }

        int capacity = Math.Max(required, Math.Max(64 * 1024, _inputBuf.Length * 2));
        _inputBuf = new byte[capacity];
    }

    /// <summary>释放 <c>_ctx / _pkt / _frame</c>，幂等。</summary>
    private void ReleaseUnmanaged()
    {
        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_pkt != null)
        {
            AVPacket* pkt = _pkt;
            ffmpeg.av_packet_free(&pkt);
            _pkt = null;
        }

        if (_ctx != null)
        {
            AVCodecContext* ctx = _ctx;
            ffmpeg.avcodec_free_context(&ctx);
            _ctx = null;
        }
    }

    /// <summary>把 FFmpeg 负数错误码翻译为可读文本。</summary>
    internal static string FFmpegError(int code)
    {
        const int BufferSize = 512;
        byte* buffer = stackalloc byte[BufferSize];
        int ret = ffmpeg.av_strerror(code, buffer, BufferSize);
        string text = ret == 0 ? Marshal.PtrToStringAnsi((IntPtr)buffer) ?? string.Empty : "未知错误";
        return $"{text} (code={code})";
    }
}
