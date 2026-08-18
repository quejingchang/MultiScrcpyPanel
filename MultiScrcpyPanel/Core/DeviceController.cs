using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core;

/// <summary>
/// 控制通道写入器（架构文档 §8-T04-1）。
/// <para>
/// 所有控制消息经有界队列交给专用写线程串行发出，<b>UI 线程永不阻塞</b>：
/// <list type="bullet">
///   <item><c>ACTION_MOVE</c> 触摸消息队列满时<b>直接丢弃</b>（避免拖拽累积延迟）；</item>
///   <item>按键 / <c>ACTION_DOWN</c> / <c>ACTION_UP</c> / 滚轮 / 文本最多等待 50ms，失败记 <c>WARN</c>（不可静默丢）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class DeviceController : IDisposable
{
    /// <summary>队列容量（条）。</summary>
    public const int QueueCapacity = 256;

    /// <summary>非 MOVE 消息的入队等待上限（毫秒）。</summary>
    public const int EnqueueTimeoutMs = 50;

    private readonly string _serial;
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly BlockingCollection<byte[]> _queue =
        new(new ConcurrentQueue<byte[]>(), QueueCapacity);

    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private Thread? _writer;
    private int _stopped;
    private long _sentMessages;
    private long _droppedMessages;

    /// <summary>创建控制通道。</summary>
    /// <param name="serial">设备序列号（仅用于日志 / 线程名）。</param>
    /// <param name="socket">已连接的 control socket（本类接管其生命周期）。</param>
    public DeviceController(string serial, Socket socket)
    {
        _serial = serial ?? string.Empty;
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _socket.NoDelay = true;
        _stream = new NetworkStream(_socket, ownsSocket: false);
    }

    /// <summary>已成功写出的消息条数。</summary>
    public long SentMessages => Interlocked.Read(ref _sentMessages);

    /// <summary>因队列拥塞被丢弃的消息条数。</summary>
    public long DroppedMessages => Interlocked.Read(ref _droppedMessages);

    /// <summary>是否已停止。</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>启动写线程；重复调用无副作用。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_writer != null || IsStopped)
            {
                return;
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = $"ctl-{_serial}"
            };
            _writer.Start();
        }
    }

    /// <summary>发送一次完整按键（DOWN + UP 两条消息）。</summary>
    public void SendKey(int keycode)
    {
        Enqueue(ControlMessages.BuildKeycode(ScrcpyConstants.ACTION_KEY_DOWN, keycode), dropOnFull: false);
        Enqueue(ControlMessages.BuildKeycode(ScrcpyConstants.ACTION_KEY_UP, keycode), dropOnFull: false);
    }

    /// <summary>发送单条按键消息（DOWN 或 UP）；脚本引擎用于分步按键。</summary>
    public void SendKeycode(byte action, int keycode)
    {
        Enqueue(ControlMessages.BuildKeycode(action, keycode), dropOnFull: false);
    }

    /// <summary>发送触摸事件。</summary>
    /// <param name="action">
    /// <see cref="ScrcpyConstants.ACTION_DOWN"/> / <see cref="ScrcpyConstants.ACTION_UP"/> /
    /// <see cref="ScrcpyConstants.ACTION_MOVE"/> / <see cref="ScrcpyConstants.ACTION_CANCEL"/>。
    /// </param>
    /// <param name="x">视频坐标系 X。</param>
    /// <param name="y">视频坐标系 Y。</param>
    /// <param name="w">视频宽度。</param>
    /// <param name="h">视频高度。</param>
    public void SendTouch(byte action, int x, int y, int w, int h)
    {
        bool dropOnFull = action == ScrcpyConstants.ACTION_MOVE;
        Enqueue(ControlMessages.BuildTouch(action, x, y, w, h), dropOnFull);
    }

    /// <summary>发送滚轮事件。</summary>
    public void SendScroll(int x, int y, int w, int h, float hScroll, float vScroll)
    {
        Enqueue(ControlMessages.BuildScroll(x, y, w, h, hScroll, vScroll), dropOnFull: false);
    }

    /// <summary>发送文本注入事件。</summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Enqueue(ControlMessages.BuildText(text), dropOnFull: false);
    }

    /// <summary>停止写线程并关闭 socket；<b>幂等</b>。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        Thread? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }

        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        if (writer != null && writer.IsAlive)
        {
            try
            {
                if (!writer.Join(1000))
                {
                    Log.Warn($"[{_serial}] 控制写线程 1s 内未退出（不使用已废弃的 Thread.Abort，交由进程退出回收）。");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[{_serial}] 等待控制写线程退出异常：{ex.Message}");
            }
        }

        SafeClose();

        Log.Info($"[{_serial}] 控制通道已关闭（发出 {SentMessages} 条，丢弃 {DroppedMessages} 条）。");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();

        try
        {
            _queue.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"[{_serial}] 释放控制队列异常：{ex.Message}");
        }

        try
        {
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"[{_serial}] 释放控制 CTS 异常：{ex.Message}");
        }
    }

    /// <summary>把消息投入发送队列。</summary>
    /// <param name="payload">已编码的控制消息字节。</param>
    /// <param name="dropOnFull">队列满时是否直接丢弃（MOVE 专用）。</param>
    private void Enqueue(byte[] payload, bool dropOnFull)
    {
        if (payload.Length == 0 || IsStopped)
        {
            return;
        }

        try
        {
            bool ok = dropOnFull
                ? _queue.TryAdd(payload)
                : _queue.TryAdd(payload, EnqueueTimeoutMs);

            if (!ok)
            {
                Interlocked.Increment(ref _droppedMessages);
                if (!dropOnFull)
                {
                    Log.Warn($"[{_serial}] 控制队列拥塞，丢弃 1 条消息（{payload.Length} 字节）。");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
            // 注意：ObjectDisposedException 派生自 InvalidOperationException，必须排在前面，
            // 否则会被下面的 catch 抢先捕获（CS0160）。
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 之后入队，属于关闭竞态，静默忽略。
        }
    }

    /// <summary>写线程主循环：串行消费队列并写入 socket。</summary>
    private void WriterLoop()
    {
        try
        {
            foreach (byte[] payload in _queue.GetConsumingEnumerable(_cts.Token))
            {
                _stream.Write(payload, 0, payload.Length);
                Interlocked.Increment(ref _sentMessages);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径。
        }
        catch (ObjectDisposedException)
        {
            // 停止时 socket 已关闭。
        }
        catch (Exception ex)
        {
            Log.Error($"[{_serial}] 控制通道写入失败，写线程退出。", ex);
        }
    }

    /// <summary>关闭底层流与 socket，每步独立保护。</summary>
    private void SafeClose()
    {
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"[{_serial}] 关闭控制流异常：{ex.Message}");
        }

        try
        {
            if (_socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[{_serial}] 关闭控制 socket（shutdown）异常：{ex.Message}");
        }

        try
        {
            _socket.Close();
        }
        catch (Exception ex)
        {
            Log.Debug($"[{_serial}] 关闭控制 socket 异常：{ex.Message}");
        }
    }
}
