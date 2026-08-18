using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core;

/// <summary>
/// 视频 socket 的握手与拆包（架构文档 §5.2 / §5.3 / §8 T03-1）。
/// <para>
/// 读取严格"读满为止"：<see cref="NetworkStream"/> 允许短读，必须循环。
/// </para>
/// <para>
/// ⭐ 握手阶段 <c>ReadTimeout = <see cref="HandshakeTimeoutMs"/>（5000）</c>；调用
/// <see cref="EnterStreamingMode"/> 后置为 <c>Timeout.Infinite</c>——MediaCodec 仅在画面变化时
/// 产出数据，静态画面下固定超时会被误判掉线（修正 Python 版隐患，架构文档 §11-#7）。
/// 退出靠其他线程调用 <see cref="Close"/> 主动打断阻塞读。
/// </para>
/// <para>
/// ⭐ <b>为什么握手超时保持 5s（而非 30s）</b>：曾经一度把它放宽到 30s，当时误判为
/// 「dummy 已到但设备名迟到是设备端初始化 / ADB 转发层缓冲慢」。真正的根因是握手<b>顺序</b>
/// 错误——<see cref="DeviceSession.StreamLoop"/> 旧实现在只连了 video socket 时就去阻塞读设备名，
/// 而 scrcpy v4.0 服务端要等 control socket 也 accept 完才会写 device meta，双方互等导致死锁，
/// 表现出「读不到设备名」。顺序修正后（先连 control socket 并启动 <c>DeviceController</c>，
/// 再读设备名），device meta 会立即到达，正常握手 <b>&lt; 1 秒</b>。
/// 因此握手超时保持 5s 即可：顺序正确时设备名瞬间到达；若 5s 内仍读不到，通常意味着握手顺序
/// 错误或隧道存在真实故障，应<b>快速失败</b>暴露给用户，而不是让用户干等 30s。
/// </para>
/// <para>
/// <b>线格式（scrcpy v4.0，未改动）</b>：
/// dummy 1 字节（仅 forward，<b>设备 → 客户端</b>）→ 设备名 64 字节固定缓冲（<c>\0</c> 截断）
/// → codec id u32 大端。分辨率不在此处，走后续的 session packet。
/// </para>
/// </summary>
public sealed class VideoStreamReader : IDisposable
{
    /// <summary>
    /// 握手阶段（设备名 / codec id）的读超时（毫秒）。默认值 5000。
    /// <para>
    /// 必须显著大于 <see cref="DummyProbeTimeoutMs"/>：dummy 是「隧道是否已通」的快速探测，
    /// 失败可<b>关闭重连</b>；而设备名是「隧道已通之后」的等待，顺序正确时 device meta 会立即到达
    /// （正常握手 &lt; 1 秒）。若 5s 内仍读不到，通常意味着握手顺序错误或隧道存在真实故障，
    /// 应<b>快速失败</b>而非让用户干等——这是面向用户的故障暴露边界，而非「慢速机型兼容窗口」。
    /// </para>
    /// </summary>
    public const int HandshakeTimeoutMs = 5_000;

    /// <summary>dummy 字节探测的默认超时（毫秒）。</summary>
    public const int DummyProbeTimeoutMs = 1000;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly byte[] _header = new byte[StreamPackets.HEADER_SIZE];
    private int _closed;

    public VideoStreamReader(Socket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _socket.NoDelay = true;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _stream.ReadTimeout = HandshakeTimeoutMs;
    }

    /// <summary>
    /// forward 模式的连接可用性探测：在<b>刚建立</b>的 socket 上尝试读 1 字节 dummy。
    /// <para>
    /// ⭐ <b>为什么必须单独抽出来</b>：<c>adb forward tcp:&lt;port&gt; localabstract:scrcpy_&lt;scid&gt;</c>
    /// 一返回，adb 就在本机端口上 listen 了；而设备端 <c>app_process</c> 还在启动 JVM，
    /// 尚未 bind 抽象套接字。此时客户端 <c>connect</c> 会<b>立刻成功</b>，
    /// 但 adb 连不到抽象套接字，随即 FIN 关闭这条 TCP —— 客户端首个 read 返回 0。
    /// 因此 scrcpy 上游把「connect + 读 dummy」合成一个可重试单元
    /// （<c>connect_and_read_byte()</c>，100 次 × 100ms）。
    /// 本方法就是那个 <c>read_byte</c>：失败时调用方应<b>关闭 socket 重连</b>，而不是判定会话失败。
    /// </para>
    /// <para>本方法不改变 dummy 的方向（始终是设备 → 客户端），只改变失败后的处置方式。</para>
    /// </summary>
    /// <param name="socket">刚 connect 成功的 socket。</param>
    /// <param name="timeoutMs">读超时毫秒；&lt;= 0 时使用 <see cref="DummyProbeTimeoutMs"/>。</param>
    /// <param name="failureReason">失败原因（成功时为空串）。</param>
    /// <returns>成功读到 dummy 字节返回 <c>true</c>。</returns>
    public static bool TryReadDummyByte(Socket socket, int timeoutMs, out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(socket);

        int effectiveTimeout = timeoutMs > 0 ? timeoutMs : DummyProbeTimeoutMs;
        byte[] buffer = new byte[1];

        try
        {
            socket.ReceiveTimeout = effectiveTimeout;
            int n = socket.Receive(buffer, 0, 1, SocketFlags.None);
            if (n == 1)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = "隧道另一端尚未就绪（read 返回 0，adb 已关闭连接）";
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            failureReason = $"等待 dummy 字节超时（{effectiveTimeout}ms）";
            return false;
        }
        catch (SocketException ex)
        {
            failureReason = $"socket 错误 {ex.SocketErrorCode}：{ex.Message}";
            return false;
        }
        catch (ObjectDisposedException)
        {
            failureReason = "socket 已被释放";
            return false;
        }
    }

    /// <summary>
    /// 握手：forward 模式先读 1 字节 dummy，再读 64 字节设备名（\0 截断后 UTF-8 解码）。
    /// <para>
    /// 调用方若已通过 <see cref="TryReadDummyByte"/> 在连接阶段消费掉 dummy，
    /// 应改用 <see cref="ReadDeviceName"/>（等价于 <c>ReadHandshake(false)</c>）。
    /// </para>
    /// </summary>
    public string ReadHandshake(bool isForward)
    {
        if (isForward)
        {
            Span<byte> dummy = stackalloc byte[1];
            ReceiveExact(dummy, "dummy 字节");
        }

        return ReadDeviceName();
    }

    /// <summary>读取 64 字节固定缓冲的设备名（<c>\0</c> 截断后 UTF-8 解码）。</summary>
    public string ReadDeviceName()
    {
        byte[] nameBuf = new byte[ScrcpyConstants.DEVICE_NAME_SIZE];
        ReceiveExact(nameBuf.AsSpan(), "设备名");

        int end = Array.IndexOf(nameBuf, (byte)0);
        if (end < 0) end = nameBuf.Length;
        return Encoding.UTF8.GetString(nameBuf, 0, end).Trim();
    }

    /// <summary>读取 4 字节大端 codec id。</summary>
    /// <exception cref="ProtocolException">codec id 不在支持列表内。</exception>
    public uint ReadCodecId()
    {
        Span<byte> buf = stackalloc byte[4];
        ReceiveExact(buf, "codec id");
        uint codecId = BinaryPrimitives.ReadUInt32BigEndian(buf);

        if (codecId != ScrcpyConstants.CODEC_H264 &&
            codecId != ScrcpyConstants.CODEC_H265 &&
            codecId != ScrcpyConstants.CODEC_AV1)
        {
            throw new ProtocolException($"不支持的 codec id：0x{codecId:X8}");
        }

        return codecId;
    }

    /// <summary>握手完成后调用：解除读超时，避免静态画面被误判掉线。</summary>
    public void EnterStreamingMode()
    {
        try
        {
            _stream.ReadTimeout = Timeout.Infinite;
        }
        catch (Exception ex)
        {
            Log.Warn($"设置 ReadTimeout=Infinite 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读取一个视频流包：先读 12 字节头，session 包直接返回，media 包继续读满载荷。
    /// </summary>
    public PacketKind ReadPacket(out SessionPacket session, out MediaPacket media)
    {
        session = default;
        media = default;

        ReceiveExact(_header.AsSpan(), "包头");

        if (StreamPackets.IsSessionPacket(_header))
        {
            session = StreamPackets.ParseSession(_header);
            return PacketKind.Session;
        }

        int size = StreamPackets.ParseMediaHeader(_header, out bool isConfig, out bool isKeyFrame, out long pts);
        if (size < 0 || size > 64 * 1024 * 1024)
        {
            throw new ProtocolException($"媒体包长度非法：{size} 字节");
        }

        byte[] payload = size > 0 ? new byte[size] : Array.Empty<byte>();
        if (size > 0) ReceiveExact(payload.AsSpan(), "媒体包载荷");

        media = new MediaPacket(isConfig, isKeyFrame, pts, payload);
        return PacketKind.Media;
    }

    /// <summary>循环读满目标缓冲区。</summary>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="what">正在读取的内容名称，仅用于错误信息定位。</param>
    /// <exception cref="ProtocolException">对端关闭连接或读超时。</exception>
    private void ReceiveExact(Span<byte> buffer, string what)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n;
            try
            {
                n = _stream.Read(buffer[got..]);
            }
            catch (IOException ex) when (ex.InnerException is SocketException se
                                         && se.SocketErrorCode == SocketError.TimedOut)
            {
                throw new ProtocolException(
                    $"读取{what}超时（已读 {got}/{buffer.Length} 字节）。", ex);
            }

            if (n <= 0)
            {
                throw new ProtocolException(
                    $"读取{what}时对端关闭连接（read 返回 0，已读 {got}/{buffer.Length} 字节）");
            }

            got += n;
        }
    }

    /// <summary>关闭底层 socket（幂等）。供其他线程打断阻塞读。</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        try { _socket.Shutdown(SocketShutdown.Both); } catch { /* 可能已断开 */ }
        try { _stream.Dispose(); } catch { /* 尽力清理 */ }
        try { _socket.Close(); } catch { /* 尽力清理 */ }
    }

    public void Dispose() => Close();
}
