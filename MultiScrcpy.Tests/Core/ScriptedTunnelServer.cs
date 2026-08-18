using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// 用真实 TCP loopback 复刻 <c>adb forward</c> 的竞态行为，供无头测试使用。
/// <para>
/// <b>为什么需要它</b>：真机上的故障是「<c>adb forward</c> 已在本机 listen，但设备端
/// <c>app_process</c> 还没 bind 抽象套接字」——客户端 connect 立刻成功，随即被 FIN。
/// 这个类通过「前 <c>failFirst</c> 条连接接受后立即关闭，之后的连接正常发握手数据」
/// 精确复刻该时序，不需要真机、不需要 adb。
/// </para>
/// <para>
/// ⭐ <b>deviceNameDelay</b>：复刻第二个真机故障——dummy 字节已送达（隧道已通），
/// 但 64 字节设备名因设备端初始化 / ADB 转发层缓冲而滞后数秒才到。
/// 用于验证客户端握手超时是否足够宽裕。
/// </para>
/// </summary>
internal sealed class ScriptedTunnelServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentBag<TcpClient> _served = new();
    private readonly ConcurrentBag<Task> _serveTasks = new();
    private readonly int _failFirst;
    private readonly byte[] _handshake;
    private readonly TimeSpan _deviceNameDelay;

    private int _acceptedCount;

    /// <param name="failFirst">前多少条连接「接受后立即关闭」（模拟 adb 连不到抽象套接字后的 FIN）。</param>
    /// <param name="deviceName">就绪后回送的设备名。</param>
    /// <param name="codecId">就绪后回送的 codec id（大端 u32）。</param>
    /// <param name="deviceNameDelay">
    /// 发完 dummy 字节后、发送设备名之前的人为延迟；
    /// <c>default</c> / 非正值表示一次性发完整个握手载荷（原行为）。
    /// </param>
    public ScriptedTunnelServer(
        int failFirst,
        string deviceName = "ANA-AN00",
        uint codecId = ScrcpyConstants.CODEC_H264,
        TimeSpan deviceNameDelay = default)
    {
        _failFirst = failFirst;
        _handshake = BuildHandshake(deviceName, codecId);
        _deviceNameDelay = deviceNameDelay > TimeSpan.Zero ? deviceNameDelay : TimeSpan.Zero;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>实际监听的本机端口。</summary>
    public int Port { get; }

    /// <summary>累计被接受的连接数（用于断言「确实重试过」）。</summary>
    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    /// <summary>
    /// 握手载荷：dummy 1 字节（设备 → 客户端）+ 设备名 64 字节固定缓冲（\0 补齐）+ codec id u32 大端。
    /// <para>方向与字段宽度与 scrcpy v4.0 线格式一致，测试据此反向校验客户端未把方向读反。</para>
    /// </summary>
    public static byte[] BuildHandshake(string deviceName, uint codecId)
    {
        var payload = new byte[1 + ScrcpyConstants.DEVICE_NAME_SIZE + 4];

        payload[0] = 0x00;   // dummy

        byte[] nameBytes = Encoding.UTF8.GetBytes(deviceName);
        int copy = Math.Min(nameBytes.Length, ScrcpyConstants.DEVICE_NAME_SIZE - 1);
        Buffer.BlockCopy(nameBytes, 0, payload, 1, copy);
        // 其余字节保持 0，等价于 \0 补齐。

        int codecOffset = 1 + ScrcpyConstants.DEVICE_NAME_SIZE;
        payload[codecOffset + 0] = (byte)(codecId >> 24);
        payload[codecOffset + 1] = (byte)(codecId >> 16);
        payload[codecOffset + 2] = (byte)(codecId >> 8);
        payload[codecOffset + 3] = (byte)codecId;

        return payload;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            int index = Interlocked.Increment(ref _acceptedCount);

            if (index <= _failFirst)
            {
                // 复刻 adb 的 FIN：不发任何数据，优雅关闭 → 客户端首个 read 返回 0。
                try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* 已断开 */ }
                client.Dispose();
                continue;
            }

            _served.Add(client);

            // 派发到独立任务：deviceNameDelay > 0 时不能阻塞 accept 循环，
            // 否则同一台 server 上的后续连接会被人为串行化。
            _serveTasks.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    /// <summary>向一条已就绪的连接回送握手载荷（可选地把设备名延后发送）。</summary>
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();

            if (_deviceNameDelay <= TimeSpan.Zero)
            {
                await stream.WriteAsync(_handshake.AsMemory(), _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                return;
            }

            // 先只发 dummy（隧道已通的信号），再延迟，最后补上设备名 + codec id。
            await stream.WriteAsync(_handshake.AsMemory(0, 1), _cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);

            await Task.Delay(_deviceNameDelay, _cts.Token).ConfigureAwait(false);

            await stream.WriteAsync(_handshake.AsMemory(1), _cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 客户端可能已放弃、或 server 正在 Dispose；测试断言不依赖这里。
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 尽力清理 */ }
        try { _listener.Stop(); } catch { /* 尽力清理 */ }

        try { Task.WhenAll(_serveTasks).Wait(TimeSpan.FromSeconds(5)); } catch { /* 尽力清理 */ }

        foreach (TcpClient client in _served)
        {
            try { client.Dispose(); } catch { /* 尽力清理 */ }
        }

        try { _acceptLoop.Wait(TimeSpan.FromSeconds(5)); } catch { /* 尽力清理 */ }
        try { _cts.Dispose(); } catch { /* 尽力清理 */ }
    }
}
