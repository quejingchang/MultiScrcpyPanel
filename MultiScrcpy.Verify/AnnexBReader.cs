using System;
using System.Collections.Generic;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Verify;

/// <summary>
/// 把一段离线的 H.264 Annex-B 裸流切分成<b>与 scrcpy v4.0 服务端等价</b>的
/// <see cref="MediaPacket"/> 序列，用于在没有真机的情况下驱动 <c>H264Decoder</c>。
/// <para>
/// scrcpy 的视频流语义（架构文档 §5.3b）：
/// <list type="bullet">
///   <item><description>SPS(7) / PPS(8) 单独打成一个 <c>IsConfig=true</c> 的包；</description></item>
///   <item><description>每个访问单元（AU，即一帧）打成一个 <c>IsConfig=false</c> 的包，
///   IDR(5) 置 <c>IsKeyFrame=true</c>；</description></item>
///   <item><description>AUD(9) / SEI(6) 等非 VCL 前缀并入其后的 VCL 包，绝不单独下发。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>切帧规则</b>：一帧可能被编码成多个 slice（多个 VCL NAL）。
/// 依据 H.264 规范，slice header 的第一个语法元素是 <c>first_mb_in_slice</c>（ue(v)），
/// 其值为 0 表示新图像的起始 slice。这里用「RBSP 首字节最高位为 1 ⇒ ue(v)==0」
/// 来判定 AU 边界，保证喂给解码器的永远是完整 AU。
/// </para>
/// </summary>
internal static class AnnexBReader
{
    /// <summary>非 IDR 编码片。</summary>
    private const int NalSliceNonIdr = 1;

    /// <summary>IDR 编码片。</summary>
    private const int NalSliceIdr = 5;

    /// <summary>序列参数集。</summary>
    private const int NalSps = 7;

    /// <summary>图像参数集。</summary>
    private const int NalPps = 8;

    /// <summary>一个 NAL 单元在原始缓冲中的位置描述。</summary>
    /// <param name="Offset">含起始码的起点偏移。</param>
    /// <param name="Length">含起始码的总长度。</param>
    /// <param name="StartCodeLength">起始码长度（3 或 4）。</param>
    /// <param name="NalType">nal_unit_type（低 5 位）。</param>
    private readonly record struct NalUnit(int Offset, int Length, int StartCodeLength, int NalType);

    /// <summary>
    /// 把 Annex-B 裸流转换为 scrcpy 语义的媒体包序列。
    /// </summary>
    /// <param name="stream">完整的 Annex-B 字节流。</param>
    /// <param name="ptsStepUs">相邻帧的 PTS 步进（微秒），默认 100000（10 fps）。</param>
    /// <returns>按时间顺序排列的媒体包；流中无 VCL NAL 时返回空列表。</returns>
    public static List<MediaPacket> ToScrcpyPackets(byte[] stream, long ptsStepUs = 100_000L)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var packets = new List<MediaPacket>();
        List<NalUnit> nals = SplitNalUnits(stream);
        if (nals.Count == 0)
        {
            return packets;
        }

        var config = new List<byte>(256);    // 累积中的 SPS/PPS
        var prefix = new List<byte>(256);    // 累积中的 AUD/SEI 等非 VCL 前缀
        var au = new List<byte>(64 * 1024);  // 累积中的访问单元（可能含多 slice）

        bool auIsKeyFrame = false;
        bool auHasVcl = false;
        long pts = 0L;

        foreach (NalUnit nal in nals)
        {
            bool isVcl = nal.NalType is NalSliceNonIdr or NalSliceIdr;

            if (!isVcl)
            {
                if (nal.NalType is NalSps or NalPps)
                {
                    // SPS/PPS 归入 config；先把已累积的 AU 冲刷出去（AU 到此结束）
                    FlushAccessUnit(packets, au, ref auHasVcl, ref auIsKeyFrame, ref pts, ptsStepUs);
                    Append(config, stream, nal.Offset, nal.Length);
                }
                else
                {
                    // AUD / SEI / 其他：作为下一个 VCL 的前缀
                    if (auHasVcl)
                    {
                        FlushAccessUnit(packets, au, ref auHasVcl, ref auIsKeyFrame, ref pts, ptsStepUs);
                    }

                    Append(prefix, stream, nal.Offset, nal.Length);
                }

                continue;
            }

            // VCL：判定是否新图像的起始 slice
            bool firstSliceOfPicture = IsFirstSliceOfPicture(stream, nal);
            if (auHasVcl && firstSliceOfPicture)
            {
                FlushAccessUnit(packets, au, ref auHasVcl, ref auIsKeyFrame, ref pts, ptsStepUs);
            }

            // 新 AU 起头：先把 config 单独下发（严格对齐 scrcpy：config 永远独立成包）
            if (!auHasVcl && config.Count > 0)
            {
                packets.Add(new MediaPacket(true, false, pts, config.ToArray()));
                config.Clear();
            }

            if (prefix.Count > 0)
            {
                au.AddRange(prefix);
                prefix.Clear();
            }

            Append(au, stream, nal.Offset, nal.Length);
            auHasVcl = true;
            auIsKeyFrame |= nal.NalType == NalSliceIdr;
        }

        FlushAccessUnit(packets, au, ref auHasVcl, ref auIsKeyFrame, ref pts, ptsStepUs);

        // 流尾残留的 config 也补一个包出去：scrcpy 语义下 config 绝不丢弃
        if (config.Count > 0)
        {
            packets.Add(new MediaPacket(true, false, pts, config.ToArray()));
        }

        return packets;
    }

    /// <summary>把当前累积的 AU 打包并复位状态；无 VCL 数据时为空操作。</summary>
    private static void FlushAccessUnit(List<MediaPacket> packets, List<byte> au,
                                        ref bool auHasVcl, ref bool auIsKeyFrame,
                                        ref long pts, long ptsStepUs)
    {
        if (!auHasVcl || au.Count == 0)
        {
            au.Clear();
            auHasVcl = false;
            auIsKeyFrame = false;
            return;
        }

        packets.Add(new MediaPacket(false, auIsKeyFrame, pts, au.ToArray()));
        pts += ptsStepUs;

        au.Clear();
        auHasVcl = false;
        auIsKeyFrame = false;
    }

    /// <summary>
    /// 判定该 VCL NAL 是否为新图像的起始 slice。
    /// <para>
    /// slice header 首元素 <c>first_mb_in_slice</c> 是 ue(v)：
    /// 其值为 0 当且仅当 RBSP 第一个 bit 为 1，即首字节 <c>&amp; 0x80 != 0</c>。
    /// </para>
    /// </summary>
    private static bool IsFirstSliceOfPicture(byte[] stream, NalUnit nal)
    {
        // 布局：[起始码][NAL header 1 字节][RBSP...]
        int rbspIndex = nal.Offset + nal.StartCodeLength + 1;
        if (rbspIndex >= nal.Offset + nal.Length || rbspIndex >= stream.Length)
        {
            // 没有 RBSP 可读：保守认为是新图像，避免把两帧粘成一个包
            return true;
        }

        return (stream[rbspIndex] & 0x80) != 0;
    }

    /// <summary>扫描全部 Annex-B 起始码，切出 NAL 单元列表（含起始码）。</summary>
    private static List<NalUnit> SplitNalUnits(byte[] d)
    {
        var starts = new List<(int Offset, int StartCodeLength)>();

        for (int i = 0; i + 2 < d.Length; i++)
        {
            if (d[i] != 0x00 || d[i + 1] != 0x00 || d[i + 2] != 0x01)
            {
                continue;
            }

            // 前面若还有一个 0x00 则是 4 字节起始码
            if (i > 0 && d[i - 1] == 0x00)
            {
                starts.Add((i - 1, 4));
            }
            else
            {
                starts.Add((i, 3));
            }

            i += 2;   // 循环再 ++ → 跳过整个起始码
        }

        var nals = new List<NalUnit>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
        {
            (int offset, int startCodeLength) = starts[k];
            int end = k + 1 < starts.Count ? starts[k + 1].Offset : d.Length;
            int length = end - offset;

            // 起始码后至少要有 1 字节 NAL header
            if (length <= startCodeLength)
            {
                continue;
            }

            int nalType = d[offset + startCodeLength] & 0x1F;
            nals.Add(new NalUnit(offset, length, startCodeLength, nalType));
        }

        return nals;
    }

    /// <summary>把 <paramref name="source"/> 的一段追加到目标列表。</summary>
    private static void Append(List<byte> target, byte[] source, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            target.Add(source[offset + i]);
        }
    }
}
