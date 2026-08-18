using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using MultiScrcpy.Core;
using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// 握手读超时的回归测试（真实 TCP loopback，无 adb / 无真机）。
///
/// <para>
/// <b>历史背景（已纠正的误判）</b>：早期实现曾把
/// <see cref="VideoStreamReader.HandshakeTimeoutMs"/> 从 5000 放宽到 30000，当时误判根因为
/// 「dummy 已到、设备名迟到是设备端初始化 / ADB 转发层缓冲慢」。真正根因是握手<b>顺序</b>错误：
/// 旧 <c>DeviceSession.StreamLoop</c> 只连了 video socket 就阻塞读设备名，而 scrcpy v4.0 服务端
/// 要等 control socket 也 accept 完才写 device meta（见 <see cref="ForwardHandshakeOrderTests"/>）。
/// 顺序修正后 device meta 立即到达，正常握手 &lt; 1 秒。
/// </para>
///
/// <para>
/// <b>本文件的作用</b>：把「握手超时 = 5s」这个<b>快速失败边界</b>钉死，并守护
/// 「dummy 探测短超时 + 握手超时显著大于 dummy 超时」这组量级。只要有人把握手超时改回 30s 量级
/// （重蹈覆辙），或把 <see cref="VideoStreamReader.DummyProbeTimeoutMs"/> 放大成秒级以上，
/// 这些测试就会红。
/// </para>
/// </summary>
public class HandshakeTimeoutTests
{
    // ================================================================
    // 1. 常量守护：防止未来被改回过大的「兼容窗口」
    // ================================================================

    [Fact]
    public void 握手超时常量应为5秒级别的快速失败边界()
    {
        // 顺序修正后正常握手 < 1s；5s 仅用于快速失败暴露真实故障，
        // 而非兼容慢速机型。把它改回 30s 会掩盖握手顺序 / 隧道故障。
        Assert.Equal(
            5_000,
            VideoStreamReader.HandshakeTimeoutMs);
    }

    [Fact]
    public void dummy探测超时必须保持在秒级以内()
    {
        // dummy 探测是「重试单元」里的小超时：一轮没 ready 就该重连，不该在这里死等。
        Assert.True(
            VideoStreamReader.DummyProbeTimeoutMs > 0,
            "dummy 探测超时必须为正值，否则会退化成永久阻塞。");

        Assert.True(
            VideoStreamReader.DummyProbeTimeoutMs <= 2_000,
            $"dummy 探测超时 {VideoStreamReader.DummyProbeTimeoutMs}ms 过长——" +
            "会显著拖慢 ConnectVideoSocket 的重试频率。");
    }

    [Fact]
    public void 握手超时必须显著大于dummy探测超时()
    {
        // 语义差异：dummy 失败 = 隧道未通（可关闭重连）；设备名失败 = 隧道已通但顺序/隧道有真实故障
        // （顺序正确时应立即到达，5s 后仍未到即快速失败）。两者量级仍需拉开，避免把「等不到设备名」
        // 与「隧道偶发未通」混为一谈。5s / 1s = 5，取 3 倍作为保守下界。
        Assert.True(
            VideoStreamReader.HandshakeTimeoutMs >= VideoStreamReader.DummyProbeTimeoutMs * 3,
            $"握手超时 {VideoStreamReader.HandshakeTimeoutMs}ms 相对 dummy 探测 " +
            $"{VideoStreamReader.DummyProbeTimeoutMs}ms 不够宽裕。");
    }

    // ================================================================
    // 2. 实际生效性：构造后底层 socket 的接收超时
    // ================================================================

    [Fact]
    public void 构造VideoStreamReader后底层socket接收超时应等于握手超时常量()
    {
        using var server = new ScriptedTunnelServer(failFirst: 0);
        using Socket socket = Connect(server.Port);

        using var reader = new VideoStreamReader(socket);

        Assert.Equal(VideoStreamReader.HandshakeTimeoutMs, socket.ReceiveTimeout);
    }

    [Fact]
    public void EnterStreamingMode后应解除读超时()
    {
        // 静态画面下 MediaCodec 不产出数据，任何有限超时都会误判掉线。
        using var server = new ScriptedTunnelServer(failFirst: 0);
        using Socket socket = Connect(server.Port);

        using var reader = new VideoStreamReader(socket);
        reader.EnterStreamingMode();

        // Socket.ReceiveTimeout 用 0 表示「无限等待」。
        Assert.Equal(0, socket.ReceiveTimeout);
    }

    // ================================================================
    // 3. 正向证明：正确顺序下设备名在远小于 5s 内到达（5s 绰绰有余）
    // ================================================================

    [Fact]
    public void 握手顺序正确时设备名应在远小于5秒内读到()
    {
        // 用精确复刻 scrcpy v4.0 服务端的夹具：必须先连 control socket，server 才发 device meta。
        using var server = new ForwardServerWithControl(deviceName: "HUAWEI ANA-AN00");
        using Socket videoSocket = Connect(server.Port);

        // 连接阶段先消费 dummy（与 DeviceSession.ConnectVideoSocket 的行为一致）。
        Assert.True(
            VideoStreamReader.TryReadDummyByte(videoSocket, VideoStreamReader.DummyProbeTimeoutMs, out string reason),
            $"dummy 应立即到达，实际失败：{reason}");

        // 构造即把底层 socket 的读超时设为 HandshakeTimeoutMs（5s）。
        using var reader = new VideoStreamReader(videoSocket);

        // ⭐ 关键：先连 control socket，server 才会写出 device meta（顺序修复的核心）。
        using Socket controlSocket = Connect(server.Port);

        var watch = Stopwatch.StartNew();
        string deviceName = reader.ReadDeviceName();
        uint codecId = reader.ReadCodecId();
        watch.Stop();

        Assert.Equal("HUAWEI ANA-AN00", deviceName);
        Assert.Equal(ScrcpyConstants.CODEC_H264, codecId);

        // 顺序正确时 device meta 立即到达，耗时应为亚秒级——正向证明 5s 超时的设计前提成立。
        Assert.True(
            watch.ElapsedMilliseconds < 1000,
            $"正常握手耗费 {watch.ElapsedMilliseconds}ms，远超亚秒级预期——" +
            "「顺序正确时 device meta 立即到达」的前提被推翻，5s 超时可能不够。");
    }

    // ================================================================
    // helpers
    // ================================================================

    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        socket.Connect(IPAddress.Loopback, port);
        return socket;
    }
}
