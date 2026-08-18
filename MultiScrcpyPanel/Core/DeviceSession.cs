using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;

using MultiScrcpy.Core.Adb;
using MultiScrcpy.Core.Decoder;
using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core;

/// <summary>
/// 单台设备的完整会话编排（架构文档 §8-T04-2）：
/// 启动 server → 建隧道 → 握手 → 解码 → 渲染 → 控制通道 → 清理。
/// <para>
/// <b>⚠️ 线程模型（订阅者必读）</b>：本类的
/// <see cref="FrameAvailable"/>、<see cref="StateChanged"/>、<see cref="ResolutionChanged"/>、
/// <see cref="ErrorOccurred"/>、<see cref="ScreenshotSaved"/>
/// <b>全部在后台流线程上触发</b>，订阅者（尤其是 WinForms 控件）
/// <b>必须自行 marshal 到 UI 线程</b>（推荐 <c>UiTheme.SafePost</c>，内部走 <c>BeginInvoke</c>）。
/// </para>
/// </summary>
public sealed class DeviceSession : IDisposable
{
    /// <summary>连接重试退避序列（毫秒），总上限 <see cref="ConnectBudgetMs"/>。</summary>
    private static readonly int[] RetryDelaysMs = { 100, 200, 400, 800, 1600 };

    /// <summary>连接总预算（毫秒）。用于控制 socket——此时 server 必然已在 listen。</summary>
    public const int ConnectBudgetMs = 3000;

    /// <summary>
    /// forward 模式下「connect + 读 dummy」的总预算（毫秒）。
    /// <para>
    /// 对齐 scrcpy 上游 <c>connect_to_server(attempts=100, delay=100ms)</c>。
    /// 真机上 <c>app_process</c> 启动 JVM 常需 0.3~1s，机型差异极大（冷启动可到数秒），
    /// 原先 3s 且不重试 dummy 的策略必然踩空。
    /// </para>
    /// </summary>
    public const int HandshakeConnectBudgetMs = 10_000;

    /// <summary>forward 模式握手重试间隔（毫秒），对齐上游的 100ms。</summary>
    public const int HandshakeRetryDelayMs = 100;

    /// <summary>每尝试多少次打印一条「等待 server 就绪」进度日志。</summary>
    private const int HandshakeProgressEvery = 10;

    /// <summary>错误信息中分隔「客户端消息」与「设备端 server 输出」的标记。</summary>
    private const string ServerOutputMarker = "── 设备端 server 输出 ──";

    /// <summary>UI 未下发目标尺寸时的默认渲染宽度上限。</summary>
    private const int DefaultTargetMaxWidth = 360;

    /// <summary>UI 未下发目标尺寸时的默认渲染高度上限。</summary>
    private const int DefaultTargetMaxHeight = 720;

    private readonly AppConfig _cfg;
    private readonly ScrcpyServerLauncher _launcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleGate = new();

    private Thread? _streamThread;
    private VideoStreamReader? _reader;
    private IVideoDecoder? _decoder;
    private FrameConverter? _converter;
    private TunnelHandle? _handle;
    private Socket? _controlSocket;

    private uint _codecId;
    private int _targetW;
    private int _targetH;
    private int _appliedW;
    private int _appliedH;
    private string? _screenshotPath;
    private long _frameCounter;
    private int _stopped;
    private int _started;
    private int _serverReleased;
    private bool _disposed;

    /// <summary>创建会话（不启动线程）。</summary>
    /// <param name="info">设备信息对象（由 <see cref="DeviceManager"/> 持有并复用）。</param>
    /// <param name="launcher">server 启动器。</param>
    /// <param name="cfg">全局配置。</param>
    public DeviceSession(DeviceInfo info, ScrcpyServerLauncher launcher, AppConfig cfg)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        Serial = info.Serial;
        Frames = new FrameBuffer();
    }

    /// <summary>设备序列号。</summary>
    public string Serial { get; }

    /// <summary>设备信息（状态 / 型号 / 电量 / 分辨率）。</summary>
    public DeviceInfo Info { get; }

    /// <summary>三缓冲帧存（UI 侧通过 <c>Acquire()</c> 取最新可显示帧）。</summary>
    public FrameBuffer Frames { get; }

    /// <summary>控制通道；握手完成前为 <c>null</c>，调用方需用 <c>?.</c> 保护。</summary>
    public DeviceController? Controller { get; private set; }

    /// <summary>当前会话状态。</summary>
    public DeviceState State => Info.State;

    /// <summary>累计渲染帧数。</summary>
    public long FrameCount => Interlocked.Read(ref _frameCounter);

    /// <summary>新帧就绪（参数：serial）。<b>高频事件</b>，UI 侧必须做投递合并。</summary>
    public event Action<string>? FrameAvailable;

    /// <summary>状态变化（参数：serial, state）。</summary>
    public event Action<string, DeviceState>? StateChanged;

    /// <summary>视频分辨率变化（参数：serial, videoW, videoH）。</summary>
    public event Action<string, int, int>? ResolutionChanged;

    /// <summary>发生错误（参数：serial, message）。</summary>
    public event Action<string, string>? ErrorOccurred;

    /// <summary>截图已保存（参数：serial, 文件绝对路径）。</summary>
    public event Action<string, string>? ScreenshotSaved;

    /// <summary>启动后台流线程；重复调用无副作用。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _streamThread = new Thread(() => StreamLoop(_cts.Token))
        {
            IsBackground = true,
            Name = $"stream-{Serial}"
        };
        _streamThread.Start();
    }

    /// <summary>
    /// 由 UI 下发画面区<b>精确</b>目标尺寸（<b>不再</b>量化到 16 的倍数），流线程在下一帧生效。
    /// <para>
    /// ⭐ 「100% 缩放画面模糊」修复：旧实现用 <c>FrameConverter.Quantize</c> 把宽、高<b>各自</b>
    /// 向上取整到 16 的倍数，带来两个副作用：
    /// (1) 渲染位图尺寸（如 224x480）≠ <c>ScreenView</c> 的绘制矩形（如 211x466），
    /// GDI+ <c>DrawImage</c> 被迫做<b>第二次重采样</b>，细节被抹平；
    /// (2) 宽高独立量化<b>破坏长宽比</b>，画面被轻微拉伸后又压回，进一步发虚。
    /// 现在直接写入精确值，使「位图尺寸 == 绘制矩形尺寸」，GDI+ 走 1:1 拷贝，
    /// 全链路只保留 swscale 一次高质量缩放。
    /// </para>
    /// </summary>
    public void SetTargetSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Volatile.Write(ref _targetW, width);
        Volatile.Write(ref _targetH, height);
    }

    /// <summary>
    /// 请求在下一解码帧保存截图（<b>设备原始分辨率</b> PNG，PRD R-P1-4）。
    /// </summary>
    /// <param name="path">目标文件绝对路径。</param>
    public void RequestScreenshot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Volatile.Write(ref _screenshotPath, path);
    }

    /// <summary>停止会话并释放全部资源；<b>每步独立 try/catch、幂等</b>。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        Log.Info($"[{Serial}] 会话停止中…");

        TryStep("取消令牌", () => _cts.Cancel());
        TryStep("关闭视频流", () => _reader?.Close());   // 打断阻塞读

        Thread? thread;
        lock (_lifecycleGate)
        {
            thread = _streamThread;
            _streamThread = null;
        }

        if (thread != null && thread.IsAlive && !thread.Join(3000))
        {
            // 绝不调用已废弃的 Thread.Abort（.NET 5+ 会抛 PlatformNotSupportedException）。
            Log.Error($"[{Serial}] 流线程 3s 内未退出，放弃等待（资源交由进程退出回收）。");
        }

        // 与「握手失败」路径共用同一套清理（内部有幂等守卫，重复调用无副作用）。
        ReleaseServerResources();

        TryStep("释放帧缓冲", () => Frames.Dispose());

        SetState(DeviceState.Offline);
        Log.Info($"[{Serial}] 会话已停止（累计渲染 {FrameCount} 帧）。");
    }

    /// <summary>
    /// 释放本次会话占用的<b>设备侧与网络侧</b>资源：控制通道、解码管线、隧道与 server 进程。
    /// <para>
    /// ⭐ 握手/连接失败时也<b>必须</b>走这里。否则设备端 <c>app_process</c> 与
    /// <c>adb forward tcp:&lt;port&gt;</c> 会一直残留，用户点「刷新设备 / 全部重连」时
    /// 新旧 server 争抢同名抽象套接字，故障从「偶发」恶化成「必现」。
    /// </para>
    /// <para><b>幂等</b>：由 <see cref="_serverReleased"/> 守卫，Stop 与失败路径并发调用只生效一次。</para>
    /// </summary>
    private void ReleaseServerResources()
    {
        if (Interlocked.Exchange(ref _serverReleased, 1) != 0)
        {
            return;
        }

        TryStep("停止控制通道", () => Controller?.Dispose());
        Controller = null;

        TryStep("关闭控制 socket", () =>
        {
            _controlSocket?.Close();
            _controlSocket = null;
        });

        TryStep("释放解码器", () =>
        {
            _decoder?.Dispose();
            _decoder = null;
        });

        TryStep("释放转换器", () =>
        {
            _converter?.Dispose();
            _converter = null;
        });

        TryStep("释放视频读取器", () =>
        {
            _reader?.Dispose();
            _reader = null;
        });

        TryStep("关闭隧道与 server", () =>
        {
            _launcher.Shutdown(_handle);
            _handle = null;
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        try
        {
            _cts.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"[{Serial}] 释放会话 CTS 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 流线程主体。<b>最外层必须捕获所有异常，绝不允许异常逃逸线程。</b>
    /// </summary>
    private void StreamLoop(CancellationToken ct)
    {
        try
        {
            SetState(DeviceState.Connecting);

            _handle = _launcher.Launch(Serial);
            Log.Info($"[{Serial}] server 已启动：scid={_handle.Scid}，本地端口 {_handle.Port}。");

            // ⭐ forward 模式下 dummy 字节的读取已并入连接重试（见 ConnectVideoSocket 注释），
            //    所以后面读设备名，而不是再走一次 ReadHandshake(isForward:true)。
            Socket videoSocket = ConnectVideoSocket(_handle.Port, _cfg.TunnelForward, ct);
            _reader = new VideoStreamReader(videoSocket);

            ct.ThrowIfCancellationRequested();

            // ⭐⭐ socket#2 = control socket（无 dummy、无 meta），§5.2 步骤 4。
            //
            // 【必须在 ReadDeviceName() 之前建立，否则必定握手死锁】
            // scrcpy v4.0 `DesktopConnection` 用**同一个** LocalServerSocket 顺序 accept：
            //     video → audio → control
            // 三条 socket 全部 accept 完、退出 try-with-resources 之后，才会执行
            //     if (options.getSendDeviceMeta()) connection.sendDeviceMeta(Device.getDeviceName());
            // 把 64 字节设备名写进 **video socket**。
            //
            // 也就是说 server 端的顺序是「先收齐所有连接，再发设备名」。
            // 若客户端在只连了 video socket 的情况下就阻塞读设备名，
            // server 正卡在 `localServerSocket.accept()` 等 control socket，
            // 双方互等 → 客户端在 HandshakeTimeoutMs（当前 5s）超时后抛「读取设备名超时（已读 0/64 字节）」。
            // 因为 control socket 已在上方无条件提前建立，这条死锁路径已被消除——下面读设备名能立即拿到数据。
            //
            // reverse 模式虽由 server 主动外连，但 DesktopConnection 的
            // 「连齐所有 socket 才发 device meta」约束同样成立，故这里**无条件**提前，
            // 不做 _cfg.TunnelForward 分支——少一条分支，少一处回退风险。
            _controlSocket = ConnectWithRetry(_handle.Port, ct);
            Controller = new DeviceController(Serial, _controlSocket);
            Controller.Start();

            // 至此 server 已把 video/control 两条 socket 都 accept 完，会立刻写出设备名。
            string deviceName = _reader.ReadDeviceName();
            if (!string.IsNullOrEmpty(deviceName))
            {
                Info.DeviceName = deviceName;
                if (string.IsNullOrEmpty(Info.Model))
                {
                    Info.Model = deviceName;
                }
            }

            ct.ThrowIfCancellationRequested();

            _codecId = _reader.ReadCodecId();
            Log.Info($"[{Serial}] 视频编码：{ScrcpyConstants.CodecName(_codecId)}（设备名 {Info.DeviceName}）。");

            _decoder = new H264Decoder();
            _decoder.Open(_codecId);
            _converter = new FrameConverter(DefaultTargetMaxWidth, DefaultTargetMaxHeight, _cfg.SwsFlags);

            _reader.EnterStreamingMode();
            SetState(DeviceState.Streaming);

            PumpPackets(ct);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[{Serial}] 流线程被取消，正常退出。");
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                Log.Info($"[{Serial}] 停止期间流线程退出：{ex.Message}");
                return;
            }

            // 先取详情（内部要读 _handle 上的 server 日志），再释放资源。
            string detail = DescribeFailure(ex);

            Log.Error($"[{Serial}] 会话异常终止：{detail}", ex);

            // ⭐ 失败也必须拆隧道 + 杀设备端 server，否则残留会污染下一次连接。
            ReleaseServerResources();

            Info.LastError = detail;
            SetState(DeviceState.Error);
            RaiseError(detail);
        }
    }

    /// <summary>
    /// 把异常翻译成「用户能看懂为什么」的错误详情：在原始消息后附上设备端 server 的最近输出。
    /// </summary>
    private string DescribeFailure(Exception ex)
    {
        string message = ex.Message;
        TunnelHandle? handle = _handle;
        if (handle == null)
        {
            return message;
        }

        // ConnectVideoSocket 抛出的消息里已经拼过 server 输出，不重复追加。
        if (message.Contains(ServerOutputMarker, StringComparison.Ordinal))
        {
            return message;
        }

        var sb = new StringBuilder(message);

        if (handle.ServerExited)
        {
            sb.Append(Environment.NewLine)
              .Append($"设备端 server 承载进程已退出（退出码 {handle.ServerExitCode}）。");
        }

        string tail = handle.ServerLog.Describe();
        if (tail.Length > 0)
        {
            sb.Append(Environment.NewLine).Append(ServerOutputMarker)
              .Append(Environment.NewLine).Append(tail);
        }

        return sb.ToString();
    }

    /// <summary>读包循环：分派 Session / Media 包。</summary>
    private void PumpPackets(CancellationToken ct)
    {
        VideoStreamReader reader = _reader
            ?? throw new ProtocolException("视频读取器尚未初始化。");

        while (!ct.IsCancellationRequested)
        {
            PacketKind kind = reader.ReadPacket(out SessionPacket session, out MediaPacket media);
            if (kind == PacketKind.Session)
            {
                HandleSessionPacket(session);
            }
            else
            {
                HandleMediaPacket(media);
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 采集会话变更（旋转 / resetVideo / 折叠屏展开）：重建解码器 + 转换器 + 帧缓冲。
    /// </summary>
    private void HandleSessionPacket(SessionPacket session)
    {
        if (session.Width <= 0 || session.Height <= 0)
        {
            Log.Warn($"[{Serial}] 收到非法 session packet：{session.Width}x{session.Height}，忽略。");
            return;
        }

        Log.Info($"[{Serial}] 采集会话变更：{session.Width}x{session.Height}"
                 + $"（clientResized={session.ClientResized}），重建解码管线。");

        Info.VideoWidth = session.Width;
        Info.VideoHeight = session.Height;

        if (_decoder != null)
        {
            _decoder.Reset();
            _decoder.Open(_codecId);
        }

        // 强制下一帧重新应用目标尺寸（旋转后宽高比变化）。
        _appliedW = 0;
        _appliedH = 0;
        EnsureTargetSize(session.Width, session.Height);
        ApplyPendingTarget();

        ResolutionChanged?.Invoke(Serial, session.Width, session.Height);
    }

    /// <summary>媒体包：送解码器，帧回调内完成转换 / 发布 / 截图。</summary>
    private void HandleMediaPacket(MediaPacket media)
    {
        IVideoDecoder? decoder = _decoder;
        if (decoder == null)
        {
            return;
        }

        decoder.TryDecode(media, OnDecodedFrame);
    }

    /// <summary>解码帧回调（<b>同步消费</b>，返回后 AVFrame 会被复用）。</summary>
    private void OnDecodedFrame(IntPtr framePtr)
    {
        FrameConverter? converter = _converter;
        if (converter == null)
        {
            return;
        }

        if (Info.VideoWidth <= 0 || Info.VideoHeight <= 0)
        {
            // 极少数情况下 session packet 先于首帧丢失，用解码帧尺寸兜底。
            EnsureTargetSize(DefaultTargetMaxWidth, DefaultTargetMaxHeight);
        }

        ApplyPendingTarget();

        Bitmap? back = Frames.BeginRender();
        if (back != null)
        {
            converter.Convert(framePtr, back);
            Frames.Publish();
            Interlocked.Increment(ref _frameCounter);
            FrameAvailable?.Invoke(Serial);
        }

        TrySaveScreenshot(framePtr);
    }

    /// <summary>
    /// 通过 <c>adb shell screencap -p</c> 抓取设备原始分辨率截图。
    /// <para>
    /// 用户要求 OCR / FIND 永远使用原始设备截图，而不是从视频流解码的帧；
    /// 截图分辨率通常与手机原生分辨率一致（如 1080x2248），与模板截图完全对齐。
    /// </para>
    /// <para>
    /// 实现采用「设备临时文件 → adb pull → 本地临时文件」中转，而不是直接读 adb 子进程
    /// stdout：.NET 的 <see cref="Process.StandardOutput"/> 会预建 <see cref="StreamReader"/>
    /// 并缓冲若干字节，二进制 PNG 会被截断/损坏，导致 <c>Image.FromStream</c> 抛异常。
    /// </para>
    /// </summary>
    /// <returns>PNG 解码后的 Bitmap；adb 不可用时返回 null 并记日志。</returns>
    public Bitmap? CaptureRawScreenshot()
    {
        const string deviceTmp = "/data/local/tmp/scrOcr.png";
        string localTmp = Path.Combine(Path.GetTempPath(), $"scrOcr_{Guid.NewGuid():N}.png");

        try
        {
            AdbClient adb = _launcher.Adb;
            if (!adb.IsAvailable)
            {
                Log.Warn($"[{Serial}] 无法截图：adb 未配置。");
                return null;
            }

            // 1) 截图到设备临时文件
            adb.Run(new[] { "shell", "screencap", "-p", deviceTmp }, Serial, timeoutMs: 10000);

            // 2) 拉到本地临时文件
            adb.Run(new[] { "pull", deviceTmp, localTmp }, Serial, timeoutMs: 10000);

            if (!File.Exists(localTmp))
            {
                Log.Warn($"[{Serial}] adb pull 后本地文件不存在：{localTmp}");
                return null;
            }

            // 3) 加载为 Bitmap（立即复制到内存，随后可安全删除临时文件）
            using (var fs = new FileStream(localTmp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bitmap = (Bitmap)Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: true);
                Log.Debug($"[{Serial}] adb screencap 成功（{bitmap.Width}x{bitmap.Height}）。");
                return bitmap;
            }
        }
        catch (Exception ex)
        {
            // 输出完整异常堆栈，便于诊断设备上 screencap 失败、权限不足、路径不可写等根因
            Log.Warn($"[{Serial}] adb screencap 失败：{ex}");
            return null;
        }
        finally
        {
            try { File.Delete(localTmp); }
            catch { /* 临时文件清理失败不影响主流程 */ }
        }
    }

    /// <summary>若存在待处理的截图请求，按设备原始分辨率保存 PNG。</summary>
    private void TrySaveScreenshot(IntPtr framePtr)
    {
        string? path = Volatile.Read(ref _screenshotPath);
        if (path == null)
        {
            return;
        }

        Volatile.Write(ref _screenshotPath, null);

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using Bitmap shot = FrameConverter.ConvertToNewBitmap(framePtr);
            shot.Save(path, ImageFormat.Png);
            Log.Info($"[{Serial}] 截图已保存（{shot.Width}x{shot.Height}）：{path}");
            ScreenshotSaved?.Invoke(Serial, path);
        }
        catch (Exception ex)
        {
            Log.Error($"[{Serial}] 保存截图失败：{path}", ex);
            RaiseError($"截图保存失败：{ex.Message}");
        }
    }

    /// <summary>UI 尚未下发目标尺寸时，按视频尺寸推算一个合理默认值。</summary>
    private void EnsureTargetSize(int videoW, int videoH)
    {
        if (Volatile.Read(ref _targetW) > 0 && Volatile.Read(ref _targetH) > 0)
        {
            return;
        }

        int w = videoW > 0 ? videoW : DefaultTargetMaxWidth;
        int h = videoH > 0 ? videoH : DefaultTargetMaxHeight;

        double scale = Math.Min((double)DefaultTargetMaxWidth / w, (double)DefaultTargetMaxHeight / h);
        if (scale > 1.0)
        {
            scale = 1.0;
        }

        SetTargetSize((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }

    /// <summary>把 UI 下发的目标尺寸应用到转换器与帧缓冲（仅在变化时执行）。</summary>
    private void ApplyPendingTarget()
    {
        int w = Volatile.Read(ref _targetW);
        int h = Volatile.Read(ref _targetH);
        if (w <= 0 || h <= 0 || (w == _appliedW && h == _appliedH))
        {
            return;
        }

        _converter?.Resize(w, h);
        Frames.Resize(w, h);
        _appliedW = w;
        _appliedH = h;
        Log.Debug($"[{Serial}] 渲染目标尺寸 → {w}x{h}");
    }

    /// <summary>指数退避连接本地转发端口（0.1/0.2/0.4/0.8/1.6s，总上限 3s）。</summary>
    private Socket ConnectWithRetry(int port, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        Exception? last = null;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                socket.Connect(IPAddress.Loopback, port);
                return socket;
            }
            catch (SocketException ex)
            {
                last = ex;
                socket.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                last = ex;
            }

            if (attempt >= RetryDelaysMs.Length || watch.ElapsedMilliseconds >= ConnectBudgetMs)
            {
                break;
            }

            int delay = RetryDelaysMs[attempt];
            long remaining = ConnectBudgetMs - watch.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            if (delay > remaining)
            {
                delay = (int)remaining;
            }

            if (ct.WaitHandle.WaitOne(delay))
            {
                ct.ThrowIfCancellationRequested();
            }
        }

        throw new ProtocolException(
            $"连接本地端口 {port} 失败（已重试 {RetryDelaysMs.Length + 1} 次 / {watch.ElapsedMilliseconds}ms）："
            + (last?.Message ?? "未知原因"), last);
    }

    /// <summary>
    /// 建立视频 socket；forward 模式下把「connect + 读 dummy」作为<b>可重试单元</b>。
    /// <para>
    /// 这是修复真机握手失败的核心：<c>adb forward tcp:&lt;port&gt; localabstract:scrcpy_&lt;scid&gt;</c>
    /// 一返回，adb 就在本机端口上 listen 了，但设备端 <c>app_process</c> 还在启动 JVM、
    /// 尚未 bind 抽象套接字。此时客户端 <c>connect</c> 会<b>立刻成功</b>，
    /// 而 adb 连不到抽象套接字随即 FIN——首个 dummy 读返回 EOF。
    /// 对齐 scrcpy 上游 <c>connect_to_server(attempts=100, delay=100ms)</c>，
    /// 在 <see cref="HandshakeConnectBudgetMs"/> 预算内反复「重连 + 重读 dummy」，
    /// 直到读到一个 dummy 字节（或预算耗尽）。每轮失败只关 socket 重连，<b>绝不判会话失败</b>。
    /// </para>
    /// <para>
    /// 非 forward（reverse / 直连）模式无 dummy 字节，直接走
    /// <see cref="ConnectWithRetry"/> 即可。
    /// </para>
    /// <para>
    /// 预算耗尽时，把 <see cref="TunnelHandle.ServerLog"/> 的近期输出拼进异常信息，
    /// 让用户直接看到设备端 server 到底报了什么错。
    /// </para>
    /// </summary>
    /// <param name="port">本地转发端口。</param>
    /// <param name="isForward">是否 forward 模式（决定是否读 dummy 字节）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已握手的视频 socket（forward 模式下首个 dummy 字节已被消费）。</returns>
    private Socket ConnectVideoSocket(int port, bool isForward, CancellationToken ct)
    {
        if (!isForward)
        {
            // reverse / 直连模式：无 dummy，直接连接。
            return ConnectWithRetry(port, ct);
        }

        var watch = Stopwatch.StartNew();
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                socket.Connect(IPAddress.Loopback, port);

                bool gotDummy = VideoStreamReader.TryReadDummyByte(
                    socket, VideoStreamReader.DummyProbeTimeoutMs, out string reason);

                if (gotDummy)
                {
                    return socket;
                }

                // 连接通了但 server 还没就绪（首个 dummy 读失败）。关掉重连，不判失败。
                socket.Dispose();

                if (attempt % HandshakeProgressEvery == 0)
                {
                    Log.Info($"[{Serial}] 等待设备端 server 就绪（第 {attempt + 1} 次探测 dummy 失败：{reason}）…");
                }
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                if (attempt % HandshakeProgressEvery == 0)
                {
                    Log.Info($"[{Serial}] 等待设备端 server 就绪（第 {attempt + 1} 次连接失败：{ex.SocketErrorCode}）…");
                }
            }
            catch (ObjectDisposedException)
            {
                // socket 已被外部释放（Stop 调用），收敛异常后退出循环交给外层判定。
                socket.Dispose();
            }

            long elapsed = watch.ElapsedMilliseconds;
            if (elapsed >= HandshakeConnectBudgetMs)
            {
                break;
            }

            long remaining = HandshakeConnectBudgetMs - elapsed;
            int delay = remaining > HandshakeRetryDelayMs ? HandshakeRetryDelayMs : (int)remaining;

            if (ct.WaitHandle.WaitOne(delay))
            {
                ct.ThrowIfCancellationRequested();
            }

            attempt++;
        }

        // 预算耗尽：把设备端 server 日志拼进异常，给出可诊断的错误。
        var sb = new StringBuilder(
            $"连接视频隧道失败：forward 模式在 {HandshakeConnectBudgetMs}ms 内反复探测 dummy 字节均未成功。");

        TunnelHandle? handle = _handle;
        if (handle != null)
        {
            if (handle.ServerExited)
            {
                sb.Append(Environment.NewLine)
                  .Append($"设备端 server 承载进程已退出（退出码 {handle.ServerExitCode}）。");
            }

            string tail = handle.ServerLog.Describe();
            if (tail.Length > 0)
            {
                sb.Append(Environment.NewLine).Append(ServerOutputMarker)
                  .Append(Environment.NewLine).Append(tail);
            }
            else if (handle.ServerLog.IsEmpty)
            {
                sb.Append(Environment.NewLine)
                  .Append("设备端 server 未输出任何日志（可能未真正启动，或 adb 提前关闭了连接）。");
            }
        }

        throw new ProtocolException(sb.ToString());
    }

    /// <summary>更新状态并触发事件（状态未变化时不重复触发）。</summary>
    private void SetState(DeviceState state)
    {
        if (Info.State == state)
        {
            return;
        }

        Info.State = state;
        StateChanged?.Invoke(Serial, state);
    }

    /// <summary>触发错误事件，事件处理器抛异常不得影响会话。</summary>
    private void RaiseError(string message)
    {
        try
        {
            ErrorOccurred?.Invoke(Serial, message);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{Serial}] ErrorOccurred 订阅者抛出异常：{ex.Message}");
        }
    }

    /// <summary>执行一步清理动作，异常只记日志不外抛。</summary>
    private void TryStep(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"[{Serial}] 清理步骤「{what}」异常：{ex.Message}");
        }
    }
}
