using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// 用真实 TCP loopback 精确复刻 <b>scrcpy v4.0 服务端</b>
/// <c>com.genymobile.scrcpy.device.DesktopConnection</c> 的握手时序。
///
/// <para>
/// <b>官方源码时序（forward 模式，video + control，无 audio）</b>：
/// <code>
/// try (LocalServerSocket localServerSocket = new LocalServerSocket(socketName)) {
///     if (video) {
///         videoSocket = localServerSocket.accept();
///         if (sendDummyByte) videoSocket.getOutputStream().write(0);
///     }
///     if (audio) { audioSocket = localServerSocket.accept(); ... }
///     if (control) { controlSocket = localServerSocket.accept(); ... }
/// }
/// // ⭐ 三条 socket 全部 accept 完之后，才把 64 字节设备名写进 video socket：
/// if (options.getSendDeviceMeta()) connection.sendDeviceMeta(Device.getDeviceName());
/// </code>
/// </para>
///
/// <para>
/// <b>关键点</b>：server 用<b>同一个</b> <c>LocalServerSocket</c> 顺序 accept
/// （video → audio → control），并且<b>只有全部连接就位后</b>才发送 device meta。
/// 因此客户端如果「只连 video socket 就阻塞读设备名」，双方会互等 →
/// 客户端等满握手超时后抛「读取设备名超时（已读 0/64 字节）」，这正是本次修复的死锁。
/// （真机复现时握手超时曾被误放宽到 30s，故要干等 30s；定位到顺序根因后已改回 5s 快速失败。）
/// </para>
///
/// <para>
/// 与 <see cref="ScriptedTunnelServer"/> 的区别：后者每条连接独立回送完整握手载荷，
/// 无法暴露「必须先连 control 再读设备名」这个跨连接的顺序约束；本夹具专门锁定它。
/// </para>
/// </summary>
internal sealed class ForwardServerWithControl : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serverLoop;
    private readonly int _failFirst;
    private readonly bool _sendDummyByte;
    private readonly byte[] _deviceMeta;
    private readonly TimeSpan _metaDelay;

    private readonly ManualResetEventSlim _videoAccepted = new(false);
    private readonly ManualResetEventSlim _controlAccepted = new(false);
    private readonly ManualResetEventSlim _metaSent = new(false);

    private TcpClient? _videoClient;
    private TcpClient? _controlClient;

    private int _acceptedCount;
    private int _metaSentFlag;
    private long _controlAcceptedTicks;
    private long _metaSentTicks;

    /// <param name="failFirst">
    /// 前多少条连接「接受后立即关闭」（复刻 adb 还没连上设备端抽象套接字时的 FIN），
    /// 用于同时覆盖 <c>ConnectVideoSocket</c> 的重连逻辑。
    /// </param>
    /// <param name="deviceName">device meta 中回送的设备名。</param>
    /// <param name="codecId">device meta 之后回送的 codec id（大端 u32）。</param>
    /// <param name="sendDummyByte">是否在 video socket 上先写 1 字节 dummy（forward 模式为 <c>true</c>）。</param>
    /// <param name="metaDelay">control socket accept 之后、发送 device meta 之前的额外延迟。</param>
    public ForwardServerWithControl(
        int failFirst = 0,
        string deviceName = "ANA-AN00",
        uint codecId = ScrcpyConstants.CODEC_H264,
        bool sendDummyByte = true,
        TimeSpan metaDelay = default)
    {
        _failFirst = failFirst;
        _sendDummyByte = sendDummyByte;
        _deviceMeta = BuildDeviceMeta(deviceName, codecId);
        _metaDelay = metaDelay > TimeSpan.Zero ? metaDelay : TimeSpan.Zero;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _serverLoop = Task.Run(RunAsync);
    }

    /// <summary>实际监听的本机端口。</summary>
    public int Port { get; }

    /// <summary>累计被 accept 的连接数（含被 FIN 的那些）。</summary>
    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    /// <summary>device meta（设备名 + codec id）是否已写出。</summary>
    public bool IsMetaSent => Volatile.Read(ref _metaSentFlag) != 0;

    /// <summary>control socket 是否已被 accept。</summary>
    public bool IsControlAccepted => _controlAccepted.IsSet;

    /// <summary>
    /// device meta 是否<b>确实</b>发生在 control socket accept 之后。
    /// <para>两者都未发生时返回 <c>false</c>；这是本夹具最核心的顺序断言依据。</para>
    /// </summary>
    public bool MetaSentAfterControlAccepted
    {
        get
        {
            long control = Interlocked.Read(ref _controlAcceptedTicks);
            long meta = Interlocked.Read(ref _metaSentTicks);
            return control > 0 && meta > 0 && meta >= control;
        }
    }

    /// <summary>
    /// device meta 的固定载荷：设备名 64 字节固定缓冲（<c>\0</c> 补齐）+ codec id u32 大端。
    /// <para>方向与字段宽度与 scrcpy v4.0 线格式一致，<b>不含</b> dummy 字节。</para>
    /// </summary>
    public static byte[] BuildDeviceMeta(string deviceName, uint codecId)
    {
        var payload = new byte[ScrcpyConstants.DEVICE_NAME_SIZE + 4];

        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(deviceName ?? string.Empty);
        int copy = Math.Min(nameBytes.Length, ScrcpyConstants.DEVICE_NAME_SIZE - 1);
        Buffer.BlockCopy(nameBytes, 0, payload, 0, copy);
        // 其余字节保持 0，等价于 \0 补齐。

        int codecOffset = ScrcpyConstants.DEVICE_NAME_SIZE;
        payload[codecOffset + 0] = (byte)(codecId >> 24);
        payload[codecOffset + 1] = (byte)(codecId >> 16);
        payload[codecOffset + 2] = (byte)(codecId >> 8);
        payload[codecOffset + 3] = (byte)codecId;

        return payload;
    }

    /// <summary>等待 video socket 被 accept（并已写出 dummy）。</summary>
    public bool WaitVideoAccepted(TimeSpan timeout) => _videoAccepted.Wait(timeout);

    /// <summary>等待 control socket 被 accept。</summary>
    public bool WaitControlAccepted(TimeSpan timeout) => _controlAccepted.Wait(timeout);

    /// <summary>等待 device meta 写出。</summary>
    public bool WaitMetaSent(TimeSpan timeout) => _metaSent.Wait(timeout);

    /// <summary>
    /// 服务端主流程：严格按 <c>DesktopConnection</c> 的顺序 accept，
    /// 全部就位后才发 device meta。
    /// </summary>
    private async Task RunAsync()
    {
        try
        {
            // ---- 阶段 1：accept video socket（可能先被 FIN 若干次）----
            TcpClient? video = null;
            while (video == null)
            {
                TcpClient candidate = await AcceptAsync().ConfigureAwait(false);
                int index = Interlocked.Increment(ref _acceptedCount);

                if (index <= _failFirst)
                {
                    // 复刻 adb 连不到抽象套接字后的 FIN：不发任何数据，优雅关闭。
                    try { candidate.Client.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
                    candidate.Dispose();
                    continue;
                }

                video = candidate;
            }

            _videoClient = video;
            NetworkStream videoStream = video.GetStream();

            if (_sendDummyByte)
            {
                await videoStream.WriteAsync(new byte[] { 0x00 }.AsMemory(), _cts.Token).ConfigureAwait(false);
                await videoStream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }

            _videoAccepted.Set();

            // ---- 阶段 2：阻塞等待 control socket ----
            // ⭐ 这里绝不能发设备名。真实 server 此刻正卡在 localServerSocket.accept()。
            _controlClient = await AcceptAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _acceptedCount);
            Interlocked.Exchange(ref _controlAcceptedTicks, DateTime.UtcNow.Ticks);
            _controlAccepted.Set();

            // ---- 阶段 3：所有 socket 就位，发送 device meta（设备名 + codec id）----
            if (_metaDelay > TimeSpan.Zero)
            {
                await Task.Delay(_metaDelay, _cts.Token).ConfigureAwait(false);
            }

            await videoStream.WriteAsync(_deviceMeta.AsMemory(), _cts.Token).ConfigureAwait(false);
            await videoStream.FlushAsync(_cts.Token).ConfigureAwait(false);

            Interlocked.Exchange(ref _metaSentTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _metaSentFlag, 1);
            _metaSent.Set();
        }
        catch (OperationCanceledException)
        {
            // Dispose 期间的正常退出路径。
        }
        catch (ObjectDisposedException)
        {
            // listener 已 Stop。
        }
        catch (SocketException)
        {
            // 客户端提前放弃 / listener 已关闭。
        }
        catch (System.IO.IOException)
        {
            // 客户端在写 meta 时已断开。
        }
    }

    /// <summary>带取消支持的 accept 包装。</summary>
    private async Task<TcpClient> AcceptAsync()
    {
        return await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 尽力清理 */ }
        try { _listener.Stop(); } catch { /* 尽力清理 */ }

        try { _serverLoop.Wait(TimeSpan.FromSeconds(5)); } catch { /* 尽力清理 */ }

        try { _videoClient?.Dispose(); } catch { /* 尽力清理 */ }
        try { _controlClient?.Dispose(); } catch { /* 尽力清理 */ }

        try { _videoAccepted.Dispose(); } catch { /* 尽力清理 */ }
        try { _controlAccepted.Dispose(); } catch { /* 尽力清理 */ }
        try { _metaSent.Dispose(); } catch { /* 尽力清理 */ }
        try { _cts.Dispose(); } catch { /* 尽力清理 */ }
    }
}
