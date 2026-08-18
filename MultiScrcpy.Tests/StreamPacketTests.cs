using System;
using System.Buffers.Binary;

using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 视频流 12 字节包头解析单测，对应架构文档 §5.3。
/// <para>
/// 覆盖：session/media 判别位（bit63）、config（bit62）、keyframe（bit61）、
/// 低 61 位 PTS 掩码、载荷长度、以及包头长度不足的防御。
/// </para>
/// </summary>
public sealed class StreamPacketTests
{
    /// <summary>按 §5.3(a) 手工拼一个 session 包头。</summary>
    private static byte[] BuildSessionHeader(bool clientResized, uint width, uint height)
    {
        byte[] h = new byte[12];
        uint flags = 0x8000_0000u | (clientResized ? 1u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0, 4), flags);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8, 4), height);
        return h;
    }

    /// <summary>按 §5.3(b) 手工拼一个 media 包头。</summary>
    private static byte[] BuildMediaHeader(bool isConfig, bool isKeyFrame, ulong pts, uint size)
    {
        ulong word = pts & ((1UL << 61) - 1);
        if (isConfig) word |= 1UL << 62;
        if (isKeyFrame) word |= 1UL << 61;

        byte[] h = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(0, 8), word);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8, 4), size);
        return h;
    }

    // ---------------------------------------------------------------- 包类型判别

    [Fact]
    public void IsSessionPacket_bit7置位判为会话包()
    {
        byte[] header = { 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x04, 0x38, 0x00, 0x00, 0x09, 0xC0 };
        Assert.True(StreamPackets.IsSessionPacket(header));
    }

    [Fact]
    public void IsSessionPacket_bit7清零判为媒体包()
    {
        // config 包 byte0 = 0x40、keyframe 包 byte0 = 0x20，二者 bit7 均为 0
        Assert.False(StreamPackets.IsSessionPacket(BuildMediaHeader(true, false, 0, 128)));
        Assert.False(StreamPackets.IsSessionPacket(BuildMediaHeader(false, true, 0, 128)));
        Assert.False(StreamPackets.IsSessionPacket(BuildMediaHeader(false, false, 0, 128)));
    }

    [Fact]
    public void 媒体包头首字节的最高位恒为0()
    {
        Assert.Equal(0x40, BuildMediaHeader(true, false, 0, 0)[0]);
        Assert.Equal(0x20, BuildMediaHeader(false, true, 0, 0)[0]);
        Assert.Equal(0x60, BuildMediaHeader(true, true, 0, 0)[0]);
    }

    // ---------------------------------------------------------------- session 包

    [Fact]
    public void ParseSession_典型1080x2496_逐字段匹配()
    {
        // 0x438 = 1080，0x9C0 = 2496（折叠屏展开后的常见分辨率）
        byte[] header = { 0x80, 0x00, 0x00, 0x01, 0x00, 0x00, 0x04, 0x38, 0x00, 0x00, 0x09, 0xC0 };

        SessionPacket p = StreamPackets.ParseSession(header);

        Assert.True(p.ClientResized);
        Assert.Equal(1080, p.Width);
        Assert.Equal(2496, p.Height);
    }

    [Fact]
    public void ParseSession_clientResized标志取flags的bit0()
    {
        Assert.False(StreamPackets.ParseSession(BuildSessionHeader(false, 720, 1600)).ClientResized);
        Assert.True(StreamPackets.ParseSession(BuildSessionHeader(true, 720, 1600)).ClientResized);
    }

    [Theory]
    [InlineData(1080u, 2400u)]
    [InlineData(2400u, 1080u)]  // 横屏
    [InlineData(1u, 1u)]
    [InlineData(3840u, 2160u)]
    public void ParseSession_宽高按大端u32解析(uint w, uint h)
    {
        SessionPacket p = StreamPackets.ParseSession(BuildSessionHeader(true, w, h));
        Assert.Equal((int)w, p.Width);
        Assert.Equal((int)h, p.Height);
    }

    // ---------------------------------------------------------------- media 包

    [Fact]
    public void ParseMediaHeader_config包_bit62置位且非关键帧()
    {
        byte[] header = BuildMediaHeader(isConfig: true, isKeyFrame: false, pts: 0, size: 41);

        int size = StreamPackets.ParseMediaHeader(header, out bool isConfig, out bool isKeyFrame, out long pts);

        Assert.True(isConfig);
        Assert.False(isKeyFrame);
        Assert.Equal(0L, pts);
        Assert.Equal(41, size);
    }

    [Fact]
    public void ParseMediaHeader_关键帧_bit61置位()
    {
        byte[] header = BuildMediaHeader(isConfig: false, isKeyFrame: true, pts: 123456789UL, size: 65536);

        int size = StreamPackets.ParseMediaHeader(header, out bool isConfig, out bool isKeyFrame, out long pts);

        Assert.False(isConfig);
        Assert.True(isKeyFrame);
        Assert.Equal(123456789L, pts);
        Assert.Equal(65536, size);
    }

    [Fact]
    public void ParseMediaHeader_PTS只取低61位且不被标志位污染()
    {
        const ulong pts = 0x1F_FFFF_FFFF_FFFFUL; // 53 位，安全落在 61 位内
        byte[] header = BuildMediaHeader(isConfig: true, isKeyFrame: true, pts: pts, size: 1);

        StreamPackets.ParseMediaHeader(header, out bool isConfig, out bool isKeyFrame, out long parsed);

        Assert.True(isConfig);
        Assert.True(isKeyFrame);
        Assert.Equal((long)pts, parsed);
    }

    [Fact]
    public void ParseMediaHeader_PTS取满61位上界()
    {
        const ulong maxPts = (1UL << 61) - 1;
        byte[] header = BuildMediaHeader(isConfig: false, isKeyFrame: false, pts: maxPts, size: 0);

        // 无任何标志位时 byte0 应为 0x1F（低 61 位全 1 的最高字节）
        Assert.Equal(0x1F, header[0]);

        StreamPackets.ParseMediaHeader(header, out bool isConfig, out bool isKeyFrame, out long pts);

        Assert.False(isConfig);
        Assert.False(isKeyFrame);
        Assert.Equal(2305843009213693951L, pts);
    }

    [Fact]
    public void ParseMediaHeader_载荷长度按大端u32解析()
    {
        byte[] header = BuildMediaHeader(false, true, 0, 0x0001_0000u);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, header[8..12]);

        int size = StreamPackets.ParseMediaHeader(header, out _, out _, out _);
        Assert.Equal(65536, size);
    }

    [Fact]
    public void ParseMediaHeader_零长度载荷是合法输入()
    {
        int size = StreamPackets.ParseMediaHeader(BuildMediaHeader(false, false, 42, 0), out _, out _, out long pts);
        Assert.Equal(0, size);
        Assert.Equal(42L, pts);
    }

    // ---------------------------------------------------------------- 防御

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void 包头长度不足12字节时全部抛ArgumentException(int length)
    {
        byte[] tooShort = new byte[length];

        Assert.Throws<ArgumentException>(() => StreamPackets.IsSessionPacket(tooShort));
        Assert.Throws<ArgumentException>(() => StreamPackets.ParseSession(tooShort));
        Assert.Throws<ArgumentException>(() => StreamPackets.ParseMediaHeader(tooShort, out _, out _, out _));
    }

    [Fact]
    public void 包头超过12字节时只解析前12字节()
    {
        byte[] header = new byte[32];
        BuildSessionHeader(true, 1080, 2400).CopyTo(header, 0);
        for (int i = 12; i < header.Length; i++) header[i] = 0xAA; // 尾部噪声不得影响结果

        SessionPacket p = StreamPackets.ParseSession(header);
        Assert.Equal(1080, p.Width);
        Assert.Equal(2400, p.Height);
    }

    [Fact]
    public void HEADER_SIZE常量与协议常量同源()
    {
        Assert.Equal(12, StreamPackets.HEADER_SIZE);
        Assert.Equal(ScrcpyConstants.STREAM_HEADER_SIZE, StreamPackets.HEADER_SIZE);
    }

    // ---------------------------------------------------------------- 记录类型语义

    [Fact]
    public void 包记录结构体按值相等()
    {
        Assert.Equal(new SessionPacket(true, 1080, 2400), new SessionPacket(true, 1080, 2400));
        Assert.NotEqual(new SessionPacket(true, 1080, 2400), new SessionPacket(false, 1080, 2400));

        byte[] data = { 1, 2, 3 };
        Assert.Equal(new MediaPacket(false, true, 7, data), new MediaPacket(false, true, 7, data));
    }

    [Fact]
    public void 包类型枚举取值稳定()
    {
        Assert.Equal(0, (int)PacketKind.Session);
        Assert.Equal(1, (int)PacketKind.Media);
    }
}
