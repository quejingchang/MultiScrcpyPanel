using System;
using System.Buffers.Binary;

namespace MultiScrcpy.Protocol;

/// <summary>视频流的会话包：设备旋转 / resetVideo / 折叠屏展开时下发（架构文档 §5.3a）。</summary>
public readonly record struct SessionPacket(bool ClientResized, int Width, int Height);

/// <summary>视频流的媒体包：裸 H.264/H.265/AV1 Annex-B 数据（架构文档 §5.3b）。</summary>
public readonly record struct MediaPacket(bool IsConfig, bool IsKeyFrame, long Pts, byte[] Data);

/// <summary>视频流包类型。</summary>
public enum PacketKind
{
    /// <summary>会话包（分辨率变更）。</summary>
    Session = 0,

    /// <summary>媒体包（编码数据）。</summary>
    Media = 1
}

/// <summary>
/// 视频流 12 字节包头解析（架构文档 §5.3）。
/// <para>
/// 用 <c>header[0] &amp; 0x80</c> 区分包类型：置位为 session 包，清零为 media 包。
/// 所有整数一律大端序。本类型不依赖任何外部类型，可无头单测。
/// </para>
/// </summary>
public static class StreamPackets
{
    /// <summary>固定包头长度。</summary>
    public const int HEADER_SIZE = ScrcpyConstants.STREAM_HEADER_SIZE;

    /// <summary>media 包 PTS 占低 61 位的掩码。</summary>
    private const ulong PTS_MASK = (1UL << 61) - 1;

    /// <summary>bit62 = config packet（SPS/PPS）标志。</summary>
    private const ulong FLAG_CONFIG = 1UL << 62;

    /// <summary>bit61 = key frame 标志。</summary>
    private const ulong FLAG_KEY_FRAME = 1UL << 61;

    /// <summary>判定包类型：bit7 of byte0 置位即 session 包。</summary>
    /// <exception cref="ArgumentException">头长度不足 12 字节。</exception>
    public static bool IsSessionPacket(ReadOnlySpan<byte> header)
    {
        EnsureHeader(header);
        return (header[0] & 0x80) != 0;
    }

    /// <summary>
    /// 解析 session 包头：
    /// <c>byte0..3</c> 为 flags（bit0 = client resized），<c>byte4..7</c> 宽，<c>byte8..11</c> 高。
    /// </summary>
    public static SessionPacket ParseSession(ReadOnlySpan<byte> header)
    {
        EnsureHeader(header);
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4));
        bool clientResized = (flags & 0x1) != 0;
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4));
        return new SessionPacket(clientResized, width, height);
    }

    /// <summary>
    /// 解析 media 包头：<c>byte0..7</c> 为 u64（bit62=config、bit61=keyframe、低 61 位=PTS），
    /// <c>byte8..11</c> 为载荷字节数。
    /// </summary>
    /// <returns>载荷字节数（需继续从 socket 精确读取该长度）。</returns>
    public static int ParseMediaHeader(ReadOnlySpan<byte> header, out bool isConfig,
                                       out bool isKeyFrame, out long pts)
    {
        EnsureHeader(header);
        ulong ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(0, 8));
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4));

        isConfig = (ptsFlags & FLAG_CONFIG) != 0;
        isKeyFrame = (ptsFlags & FLAG_KEY_FRAME) != 0;
        pts = (long)(ptsFlags & PTS_MASK);
        return (int)size;
    }

    private static void EnsureHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HEADER_SIZE)
        {
            throw new ArgumentException($"视频流包头长度不足：需要 {HEADER_SIZE} 字节，实际 {header.Length} 字节。",
                                        nameof(header));
        }
    }
}
