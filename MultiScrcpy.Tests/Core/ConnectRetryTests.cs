using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// forward 模式握手重试的回归测试（真实 TCP loopback，无 adb / 无真机）。
///
/// <para>
/// <b>被锁定的缺陷</b>：<c>adb forward tcp:&lt;port&gt; localabstract:scrcpy_&lt;scid&gt;</c> 一返回，
/// adb 就在本机端口 listen 了，但设备端 <c>app_process</c> 还在启动 JVM、尚未 bind 抽象套接字。
/// 客户端 <c>connect()</c> 立刻成功，adb 连不到抽象套接字随即 FIN → 首个 dummy 读返回 0。
/// 旧实现「connect 一次 + dummy 读失败即判会话失败」，于是 UI 稳定卡在「等待画面…」。
/// </para>
///
/// <para>
/// <b>本文件的作用</b>：把「一次判死 → 预算内可重试」这个行为钉死。
/// 只要有人把 <see cref="VideoStreamReader.TryReadDummyByte"/> 改回抛异常，
/// 或把 <c>DeviceSession.ConnectVideoSocket</c> 改回单次连接，这些测试就会红。
/// </para>
/// </summary>
public class ConnectRetryTests
{
    /// <summary>单个用例的兜底时长上限，防止实现回退成死等时把 CI 挂住。</summary>
    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(30);

    // ================================================================
    // 1. TryReadDummyByte：read 返回 0 必须「返回 false」而不是抛异常
    // ================================================================

    [Fact]
    public void TryReadDummyByte_对端立即关闭时返回false而不抛异常()
    {
        using var server = new ScriptedTunnelServer(failFirst: 1);
        using Socket socket = Connect(server.Port);

        // 关键断言：这里绝不能抛。抛异常 = 上层无法重试 = 老 Bug 复发。
        bool ok = VideoStreamReader.TryReadDummyByte(socket, timeoutMs: 2000, out string reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryReadDummyByte_对端就绪时返回true且原因为空()
    {
        using var server = new ScriptedTunnelServer(failFirst: 0);
        using Socket socket = Connect(server.Port);

        bool ok = VideoStreamReader.TryReadDummyByte(socket, timeoutMs: 5000, out string reason);

        Assert.True(ok, $"应成功读到 dummy 字节，实际失败：{reason}");
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryReadDummyByte_先关后开的序列上依次返回false再true()
    {
        // 这正是真机上的时序：第 1 次连接被 FIN，第 2 次连接 server 已就绪。
        using var server = new ScriptedTunnelServer(failFirst: 1);

        using (Socket first = Connect(server.Port))
        {
            Assert.False(VideoStreamReader.TryReadDummyByte(first, 2000, out _));
        }

        using Socket second = Connect(server.Port);
        Assert.True(VideoStreamReader.TryReadDummyByte(second, 5000, out _));
    }

    [Fact]
    public void TryReadDummyByte_对端不发数据时超时返回false不抛异常()
    {
        // 用一个只 listen 不响应的 listener：连接建立但永远没有 dummy。
        var idle = new TcpListener(IPAddress.Loopback, 0);
        idle.Start();
        try
        {
            int port = ((IPEndPoint)idle.LocalEndpoint).Port;
            using Socket socket = Connect(port);

            bool ok = VideoStreamReader.TryReadDummyByte(socket, timeoutMs: 300, out string reason);

            Assert.False(ok);
            Assert.Contains("超时", reason);
        }
        finally
        {
            idle.Stop();
        }
    }

    [Fact]
    public void TryReadDummyByte_socket为null时抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => VideoStreamReader.TryReadDummyByte(null!, 100, out _));
    }

    // ================================================================
    // 2. ConnectVideoSocket：预算内反复「重连 + 重读 dummy」直到成功
    // ================================================================

    [Fact]
    public void ConnectVideoSocket_首连被FIN后重试成功并已消费dummy()
    {
        using var server = new ScriptedTunnelServer(failFirst: 1, deviceName: "ANA-AN00");
        using var session = CreateSession();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using Socket socket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        watch.Stop();

        Assert.True(socket.Connected);
        Assert.True(watch.Elapsed < CaseBudget, $"耗时 {watch.ElapsedMilliseconds}ms 明显异常");
        Assert.True(server.AcceptedCount >= 2,
            $"应至少发生 2 次连接（首连被 FIN + 重连成功），实际 {server.AcceptedCount} 次——重试逻辑可能被改回单次连接。");

        // dummy 已在连接阶段被消费 → 后续应直接读到设备名，而不是又读到 dummy。
        // 这同时校验了 StreamLoop 里「ConnectVideoSocket 之后调 ReadDeviceName」的配对关系。
        using var reader = new VideoStreamReader(socket);
        Assert.Equal("ANA-AN00", reader.ReadDeviceName());
        Assert.Equal(ScrcpyConstants.CODEC_H264, reader.ReadCodecId());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    public void ConnectVideoSocket_连续多次被FIN仍能在预算内成功(int failFirst)
    {
        // 证明这是一个「循环」而不是「只重试一次」。
        using var server = new ScriptedTunnelServer(failFirst: failFirst, deviceName: "Pixel 7");
        using var session = CreateSession();

        using Socket socket = InvokeConnectVideoSocket(session, server.Port, isForward: true);

        Assert.True(socket.Connected);
        Assert.True(server.AcceptedCount >= failFirst + 1,
            $"应至少 {failFirst + 1} 次连接，实际 {server.AcceptedCount} 次。");

        using var reader = new VideoStreamReader(socket);
        Assert.Equal("Pixel 7", reader.ReadDeviceName());
    }

    [Fact]
    public void ConnectVideoSocket_非forward模式不消费dummy字节()
    {
        // reverse / 直连模式没有 dummy；若实现误在非 forward 模式也吃掉 1 字节，
        // 后续设备名就会整体错位 1 字节。
        using var server = new ScriptedTunnelServer(failFirst: 0, deviceName: "Nexus 5X");
        using var session = CreateSession();

        using Socket socket = InvokeConnectVideoSocket(session, server.Port, isForward: false);

        using var reader = new VideoStreamReader(socket);
        Assert.Equal("Nexus 5X", reader.ReadHandshake(isForward: true));
        Assert.Equal(ScrcpyConstants.CODEC_H264, reader.ReadCodecId());
    }

    [Fact]
    public void ConnectVideoSocket_取消令牌已取消时立即抛OperationCanceled()
    {
        using var server = new ScriptedTunnelServer(failFirst: int.MaxValue);
        using var session = CreateSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = Assert.ThrowsAny<Exception>(
            () => InvokeConnectVideoSocket(session, server.Port, isForward: true, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ================================================================
    // 3. 重试预算常量：对齐上游 connect_to_server(attempts=100, delay=100ms)
    // ================================================================

    [Fact]
    public void 握手重试预算常量必须足够覆盖JVM冷启动()
    {
        // 旧实现只有 3s 的 ConnectBudgetMs 且不重试 dummy，真机冷启动必然踩空。
        Assert.True(DeviceSession.HandshakeConnectBudgetMs >= 10_000,
            $"forward 握手预算 {DeviceSession.HandshakeConnectBudgetMs}ms 过短，无法覆盖 app_process 冷启动。");
        Assert.True(DeviceSession.HandshakeRetryDelayMs > 0
                    && DeviceSession.HandshakeRetryDelayMs <= 200,
            $"重试间隔 {DeviceSession.HandshakeRetryDelayMs}ms 应对齐上游的 100ms 量级。");
        Assert.True(
            DeviceSession.HandshakeConnectBudgetMs > DeviceSession.ConnectBudgetMs,
            "forward 握手预算必须显著大于普通 socket 连接预算。");
    }

    [Fact]
    public void ConnectVideoSocket方法签名保持稳定()
    {
        MethodInfo? m = typeof(DeviceSession).GetMethod(
            "ConnectVideoSocket",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(m);
        Assert.Equal(typeof(Socket), m!.ReturnType);

        ParameterInfo[] ps = m.GetParameters();
        Assert.Equal(3, ps.Length);
        Assert.Equal(typeof(int), ps[0].ParameterType);
        Assert.Equal(typeof(bool), ps[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), ps[2].ParameterType);
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

    /// <summary>
    /// 构造一个不启动任何线程、不接触 adb 的 <see cref="DeviceSession"/>。
    /// <c>AdbClient</c> 仅在真正执行命令时才碰 adb.exe，构造是纯内存操作。
    /// </summary>
    private static DeviceSession CreateSession()
    {
        var cfg = new AppConfig { AdbPath = "adb-not-used-in-this-test" };
        var launcher = new ScrcpyServerLauncher(new AdbClient(cfg.AdbPath), cfg);
        return new DeviceSession(new DeviceInfo("TEST-SERIAL"), launcher, cfg);
    }

    private static Socket InvokeConnectVideoSocket(
        DeviceSession session, int port, bool isForward, CancellationToken ct = default)
    {
        MethodInfo m = typeof(DeviceSession).GetMethod(
            "ConnectVideoSocket",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 DeviceSession.ConnectVideoSocket——重试逻辑可能已被移除。");

        try
        {
            return (Socket)m.Invoke(session, new object[] { port, isForward, ct })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }
}
