using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Protocol;

namespace MultiScrcpy.UI;

/// <summary>
/// 单台设备的卡片（架构文档 §8-T05-3）：标题栏 + 画面区 + 两行控制按键。
/// <para>
/// 卡片<b>只与 <see cref="DeviceSession"/> / <see cref="DeviceManager"/> 交互</b>，
/// 不接触 socket / 进程 / FFmpeg。
/// </para>
/// </summary>
public sealed class DeviceCard : UserControl
{
    /// <summary>标题栏高度（像素）。</summary>
    public const int TitleHeight = 26;

    /// <summary>按键区高度（像素）。</summary>
    public const int ButtonAreaHeight = 64;

    /// <summary>「重试」按钮防连点时长（毫秒）。</summary>
    public const int RetryCooldownMs = 3000;

    private const string UnauthorizedGuide = "设备未授权\n请在手机上勾选「始终允许」并点击「允许」";

    private readonly DeviceManager _manager;
    private readonly AppConfig _cfg;
    private readonly ToolTip _tips = new();

    private readonly TableLayoutPanel _root;
    private readonly Label _title;
    private readonly ScreenView _screen;
    private readonly TableLayoutPanel _buttons;
    private readonly Button _retryButton;
    private readonly System.Windows.Forms.Timer _retryCooldown;

    private DeviceSession? _session;
    private DeviceInfo _info;
    private int _invokePending;
    private bool _unauthorized;

    /// <summary>当前正在等待「坐标录取」点击的回调（null 表示未处于录取模式）。</summary>
    private Action<double, double>? _captureCallback;

    /// <summary>当前缩放比例（由 <see cref="ApplyScale"/> 记住，方向变化时需要复用）。</summary>
    private double _scale = 1.0;

    /// <summary>最近一次已知的设备视频帧宽；<c>&lt;= 0</c> 表示方向未知（按竖屏处理）。</summary>
    private int _videoW;

    /// <summary>最近一次已知的设备视频帧高。</summary>
    private int _videoH;

    /// <summary>创建卡片。</summary>
    /// <param name="info">设备信息。</param>
    /// <param name="manager">设备管理器（用于重新授权）。</param>
    public DeviceCard(DeviceInfo info, DeviceManager manager)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _cfg = manager.Config;
        Serial = info.Serial;

        BackColor = Color.White;
        Padding = new Padding(1);
        Margin = new Padding(6);

        // ⭐ 横屏适配：卡片尺寸由「基准尺寸 + 缩放 + 设备方向」共同决定，
        //    不再写死成竖长矩形（方向未知时结果与旧实现一致）。
        _videoW = info.VideoWidth;
        _videoH = info.VideoHeight;
        Size = DeviceCardLayout.ComputeCardSize(_cfg.CardBaseWidth, _cfg.CardBaseHeight,
                                                _scale, _videoW, _videoH);

        BorderStyle = BorderStyle.FixedSingle;
        DoubleBuffered = true;

        _title = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = UiTheme.TitleFont,
            ForeColor = UiTheme.TitleForeground,
            BackColor = UiTheme.TitleOffline,
            Padding = new Padding(6, 0, 6, 0),
            AutoEllipsis = true,
            Margin = Padding.Empty
        };

        _screen = new ScreenView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };

        _retryButton = UiTheme.CreateActionButton("重新授权", "重新触发 adb reconnect 并尝试接入设备", _tips);
        _retryButton.Visible = false;
        _retryButton.Click += OnRetryAuthorize;

        _retryCooldown = new System.Windows.Forms.Timer { Interval = RetryCooldownMs };
        _retryCooldown.Tick += OnRetryCooldownElapsed;

        _buttons = BuildButtonPanel();

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, TitleHeight));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonAreaHeight));
        _root.Controls.Add(_title, 0, 0);
        _root.Controls.Add(_screen, 0, 1);
        _root.Controls.Add(_buttons, 0, 2);

        Controls.Add(_root);

        _screen.LetterboxChanged += OnLetterboxChanged;
        _screen.CoordinateCaptured += OnCoordinateCaptured;

        UpdateStatus(info);
    }

    /// <summary>设备序列号。</summary>
    public string Serial { get; }

    /// <summary>需要向用户提示的消息（文本, 级别）。</summary>
    public event Action<string, ToastLevel>? Notify;

    /// <summary>需要写入状态栏的消息。</summary>
    public event Action<string>? StatusMessage;

    /// <summary>当前是否处于「待授权」展示态。</summary>
    public bool IsUnauthorized => _unauthorized;

    /// <summary>是否已绑定会话。</summary>
    public bool IsBound => _session != null;

    /// <summary>绑定会话并接线全部事件（重复绑定会先解绑旧会话）。</summary>
    public void Bind(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        Unbind();
        _session = session;

        // ⭐ §1.6 投递合并：高频 FrameAvailable 只保留最新一次 Invalidate，避免 UI 消息队列被淹没。
        session.FrameAvailable += OnFrameAvailable;
        session.ResolutionChanged += OnResolutionChanged;
        session.StateChanged += OnSessionStateChanged;
        session.ScreenshotSaved += OnScreenshotSaved;

        _screen.Touched += OnScreenTouched;
        _screen.Scrolled += OnScreenScrolled;

        SetUnauthorized(false);
        _screen.SetSource(session.Frames, session.Info.VideoWidth, session.Info.VideoHeight);

        // ⭐ 只有已拿到真实分辨率才下发目标尺寸；否则 letterbox 无法按长宽比计算，
        //    下发的尺寸会把画面按错误比例建缓冲（模糊源头之一）。
        //    分辨率到达后 OnResolutionChanged 会重新 SetSource + SetTargetSize。
        if (session.Info.VideoWidth > 0 && session.Info.VideoHeight > 0)
        {
            // 重连场景下设备可能已经处于横屏，先把卡片翻到正确方向再算 letterbox。
            ApplyOrientation(session.Info.VideoWidth, session.Info.VideoHeight);

            Rectangle box = _screen.CurrentLetterbox();
            if (box.Width > 0 && box.Height > 0)
            {
                session.SetTargetSize(box.Width, box.Height);
            }
        }
    }

    /// <summary>解绑当前会话（幂等）。</summary>
    public void Unbind()
    {
        DeviceSession? session = _session;
        _session = null;
        if (session == null)
        {
            return;
        }

        session.FrameAvailable -= OnFrameAvailable;
        session.ResolutionChanged -= OnResolutionChanged;
        session.StateChanged -= OnSessionStateChanged;
        session.ScreenshotSaved -= OnScreenshotSaved;

        _screen.Touched -= OnScreenTouched;
        _screen.Scrolled -= OnScreenScrolled;
        _screen.SetSource(null, 0, 0);
    }

    /// <summary>刷新标题栏与按钮可用性（<b>必须在 UI 线程调用</b>）。</summary>
    public void UpdateStatus(DeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        _info = info;

        if (info.State == DeviceState.Unauthorized)
        {
            SetUnauthorized(true);
            return;
        }

        if (_unauthorized)
        {
            SetUnauthorized(false);
        }

        // ⭐ 任务 E：错误态把详情打到画面区，替代卡死的「等待画面…」；
        //    其余状态一律回到默认占位（完整错误在标题 tooltip 与日志里都有）。
        if (info.State == DeviceState.Error)
        {
            _screen.SetPlaceholder(FormatCardError(info.LastError));
        }
        else
        {
            _screen.ClearPlaceholder();
        }

        _title.Text = info.DisplayTitle();
        _title.BackColor = UiTheme.TitleColorFor(info);
        _tips.SetToolTip(_title, $"{info.Serial}\n状态：{DescribeState(info.State)}\n{info.LastError}".TrimEnd());
        _buttons.Enabled = info.State is DeviceState.Detected or DeviceState.Connecting or DeviceState.Streaming;
    }

    /// <summary>
    /// 切换「待授权」展示态（PRD Q7）：橙色标题 + 引导文案 + 禁用按键区 + 显示重试按钮。
    /// </summary>
    public void SetUnauthorized(bool unauthorized)
    {
        _unauthorized = unauthorized;

        if (unauthorized)
        {
            _title.Text = $"{Serial} | 待授权";
            _title.BackColor = UiTheme.TitleUnauthorized;
            _screen.SetPlaceholder(UnauthorizedGuide);

            foreach (Control c in _buttons.Controls)
            {
                c.Enabled = ReferenceEquals(c, _retryButton);
            }

            _buttons.Enabled = true;
            _retryButton.Visible = true;
            _retryButton.Enabled = true;
            _retryButton.Text = "重新授权";
            return;
        }

        _retryCooldown.Stop();
        _retryButton.Visible = false;
        _retryButton.Text = "重新授权";
        _retryButton.Enabled = true;

        foreach (Control c in _buttons.Controls)
        {
            c.Enabled = true;
        }

        _buttons.Enabled = true;
        _screen.ClearPlaceholder();
        _title.Text = _info.DisplayTitle();
        _title.BackColor = UiTheme.TitleColorFor(_info);
    }

    /// <summary>
    /// 按当前缩放比例<b>与设备当前方向</b>调整卡片尺寸。
    /// <para>
    /// 竖屏（含方向未知）时结果与横屏适配前完全一致，保住既有 240×600 长屏优化；
    /// 横屏时卡片翻成横长，画面区直接等于设备比例，无大黑边、无形变。
    /// </para>
    /// </summary>
    /// <param name="scale">缩放比例（0.5 / 0.75 / 1.0 / 1.5 / 2.0）。</param>
    public void ApplyScale(double scale)
    {
        _scale = scale;
        UpdateCardSize();
    }

    /// <summary>
    /// 记录设备最新视频尺寸并在<b>方向或比例发生变化</b>时重排卡片（<b>必须在 UI 线程调用</b>）。
    /// </summary>
    /// <param name="videoW">设备视频帧宽。</param>
    /// <param name="videoH">设备视频帧高。</param>
    public void ApplyOrientation(int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0)
        {
            return;
        }

        if (videoW == _videoW && videoH == _videoH)
        {
            return;
        }

        _videoW = videoW;
        _videoH = videoH;
        UpdateCardSize();
    }

    /// <summary>
    /// 在设备画面上叠加 OCR 命中标记（归一化视频坐标 x1..x2 / y1..y2 ∈ 0–1）。
    /// 多图标交集由脚本计算后传入；标记约显示 0.5s 后自动消失。
    /// </summary>
    public void ShowOcrMarker(double nx1, double ny1, double nx2, double ny2)
        => _screen.ShowOcrMarker(nx1, ny1, nx2, ny2);

    /// <summary>
    /// 进入坐标录取模式：用户在设备画面上点击一次后，归一化坐标 (nx, ny ∈ [0,1]) 回传给 onCaptured。
    /// 录取成功后自动退出模式；若需中途取消可调用 <see cref="CancelCoordinateCapture"/>。
    /// </summary>
    public void BeginCoordinateCapture(Action<double, double> onCaptured)
    {
        _captureCallback = onCaptured ?? throw new ArgumentNullException(nameof(onCaptured));
        _screen.BeginCoordinateCapture();
    }

    /// <summary>取消坐标录取（例如编辑器关闭时仍有未完成的录取）。</summary>
    public void CancelCoordinateCapture()
    {
        _captureCallback = null;
        _screen.CancelCoordinateCapture();
    }

    /// <summary>画面区录取到坐标 → 解除模式并回传给调用方。</summary>
    private void OnCoordinateCaptured(double nx, double ny)
    {
        Action<double, double>? cb = _captureCallback;
        _captureCallback = null;
        _screen.CancelCoordinateCapture();
        cb?.Invoke(nx, ny);
    }

    /// <summary>按当前缩放与方向重算卡片尺寸（尺寸未变则不触发布局）。</summary>
    private void UpdateCardSize()
    {
        Size target = DeviceCardLayout.ComputeCardSize(_cfg.CardBaseWidth, _cfg.CardBaseHeight,
                                                       _scale, _videoW, _videoH);
        if (target == Size)
        {
            return;
        }

        // 尺寸变化会级联触发 ScreenView.OnResize → LetterboxChanged → SetTargetSize，
        // 解码目标尺寸随之对齐新画面区，全链路仍是「位图尺寸 == 绘制矩形」的 1:1 拷贝。
        Size = target;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Unbind();
            _screen.LetterboxChanged -= OnLetterboxChanged;
            _screen.CoordinateCaptured -= OnCoordinateCaptured;
            _retryButton.Click -= OnRetryAuthorize;
            _retryCooldown.Tick -= OnRetryCooldownElapsed;
            _retryCooldown.Stop();
            _retryCooldown.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>构建 2 行 × 4 列的按键区。</summary>
    private TableLayoutPanel BuildButtonPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(2)
        };

        for (int i = 0; i < 4; i++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }

        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        Button home = UiTheme.CreateActionButton("主页", "HOME（KEYCODE_HOME）", _tips);
        Button back = UiTheme.CreateActionButton("返回", "BACK（KEYCODE_BACK）", _tips);
        Button recent = UiTheme.CreateActionButton("任务", "多任务 · APP_SWITCH（KEYCODE_APP_SWITCH）", _tips);
        Button power = UiTheme.CreateActionButton("电源", "POWER（KEYCODE_POWER）", _tips);
        Button volDown = UiTheme.CreateActionButton("音量-", "VOLUME_DOWN（KEYCODE_VOLUME_DOWN）", _tips);
        Button volUp = UiTheme.CreateActionButton("音量+", "VOLUME_UP（KEYCODE_VOLUME_UP）", _tips);
        Button shot = UiTheme.CreateActionButton("截图", "保存设备原始分辨率 PNG 截图", _tips);

        home.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_HOME);
        back.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_BACK);
        recent.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_APP_SWITCH);
        power.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_POWER);
        volDown.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_VOLUME_DOWN);
        volUp.Click += (_, _) => SendKey(ScrcpyConstants.KEYCODE_VOLUME_UP);
        shot.Click += (_, _) => OnScreenshot();

        panel.Controls.Add(home, 0, 0);
        panel.Controls.Add(back, 1, 0);
        panel.Controls.Add(recent, 2, 0);
        panel.Controls.Add(power, 3, 0);
        panel.Controls.Add(volDown, 0, 1);
        panel.Controls.Add(volUp, 1, 1);
        panel.Controls.Add(shot, 2, 1);
        panel.Controls.Add(_retryButton, 3, 1);

        return panel;
    }

    /// <summary>按键注入。</summary>
    private void SendKey(int keycode)
    {
        DeviceController? controller = _session?.Controller;
        if (controller == null)
        {
            Notify?.Invoke($"{Serial} 尚未连接，按键未发送", ToastLevel.Warn);
            return;
        }

        controller.SendKey(keycode);
    }

    /// <summary>截图（保存到配置目录，文件名 <c>{serial}_{yyyyMMdd_HHmmss}.png</c>）。</summary>
    private void OnScreenshot()
    {
        DeviceSession? session = _session;
        if (session == null || session.State != DeviceState.Streaming)
        {
            Notify?.Invoke($"{Serial} 尚未开始投影，无法截图", ToastLevel.Warn);
            return;
        }

        string dir = string.IsNullOrWhiteSpace(_cfg.ScreenshotDir)
            ? AppConfig.DefaultScreenshotDir()
            : _cfg.ScreenshotDir;

        string path = Path.Combine(dir, $"{Serial}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        session.RequestScreenshot(path);
        StatusMessage?.Invoke($"截图已请求：{path}");
    }

    /// <summary>「重新授权」按钮（非模态，3 秒防连点）。</summary>
    private void OnRetryAuthorize(object? sender, EventArgs e)
    {
        _retryButton.Enabled = false;
        _retryButton.Text = "重试中…";
        _retryCooldown.Stop();
        _retryCooldown.Start();

        _manager.RetryAuthorize(Serial);
        StatusMessage?.Invoke($"{Serial}：已发起重新授权，请在手机上点击「允许」。");
    }

    /// <summary>防连点计时结束，恢复按钮。</summary>
    private void OnRetryCooldownElapsed(object? sender, EventArgs e)
    {
        _retryCooldown.Stop();
        if (!_unauthorized)
        {
            return;
        }

        _retryButton.Enabled = true;
        _retryButton.Text = "重新授权";
    }

    /// <summary>⭐ 投递合并：同一时刻最多有一次 <c>BeginInvoke</c> 在飞行。</summary>
    private void OnFrameAvailable(string serial)
    {
        if (Interlocked.Exchange(ref _invokePending, 1) != 0)
        {
            return;
        }

        this.SafePost(() =>
        {
            Interlocked.Exchange(ref _invokePending, 0);
            _screen.Invalidate();
        });
    }

    /// <summary>
    /// 分辨率变化（后台线程）→ UI 线程重设画面源<b>并按新方向翻转卡片</b>。
    /// <para>
    /// ⭐ 横屏 Bug 根因修复点：<c>DeviceSession.HandleSessionPacket</c> 在设备旋转 / resize 时
    /// 会重建解码管线并抛出本事件，但旧实现只更新画面源、<b>不动卡片尺寸</b>，
    /// 竖长画面区塞进横屏帧只能缩小居中 → 上下大黑边。这里补上
    /// <see cref="ApplyOrientation"/>，让画面区形状跟着设备方向走。
    /// </para>
    /// </summary>
    private void OnResolutionChanged(string serial, int videoW, int videoH)
    {
        DeviceSession? session = _session;
        if (session == null)
        {
            return;
        }

        this.SafePost(() =>
        {
            // 先更新画面源（ScreenView 内部的视频尺寸），再翻卡片，
            // 这样卡片 Resize 级联出来的 letterbox 已经是按新比例算的。
            _screen.SetSource(session.Frames, videoW, videoH);
            ApplyOrientation(videoW, videoH);

            Rectangle box = _screen.CurrentLetterbox();
            if (box.Width > 0 && box.Height > 0)
            {
                session.SetTargetSize(box.Width, box.Height);
            }
        });
    }

    /// <summary>会话状态变化（后台线程）→ UI 线程刷新标题。</summary>
    private void OnSessionStateChanged(string serial, DeviceState state)
    {
        this.SafePost(() => UpdateStatus(_info));
    }

    /// <summary>截图完成（后台线程）→ UI 线程提示。</summary>
    private void OnScreenshotSaved(string serial, string path)
    {
        this.SafePost(() =>
        {
            Notify?.Invoke($"截图已保存：{Path.GetFileName(path)}", ToastLevel.Info);
            StatusMessage?.Invoke($"截图已保存：{path}");
        });
    }

    /// <summary>画面区触摸 → 控制通道。</summary>
    private void OnScreenTouched(byte action, int x, int y, int w, int h)
    {
        _session?.Controller?.SendTouch(action, x, y, w, h);
    }

    /// <summary>画面区滚轮 → 控制通道。</summary>
    private void OnScreenScrolled(int x, int y, int w, int h, float hScroll, float vScroll)
    {
        _session?.Controller?.SendScroll(x, y, w, h, hScroll, vScroll);
    }

    /// <summary>画面区尺寸变化 → 下发解码目标尺寸。</summary>
    private void OnLetterboxChanged(int width, int height)
    {
        _session?.SetTargetSize(width, height);
    }

    /// <summary>状态枚举的中文描述。</summary>
    private static string DescribeState(DeviceState state) => state switch
    {
        DeviceState.Offline => "离线",
        DeviceState.Unauthorized => "待授权",
        DeviceState.Detected => "已识别",
        DeviceState.Connecting => "连接中",
        DeviceState.Streaming => "投影中",
        DeviceState.Error => "错误",
        _ => state.ToString()
    };

    /// <summary>把错误压缩成卡片画面区可显示的短文本（完整信息见标题 tooltip / 日志）。</summary>
    private static string FormatCardError(string? lastError)
    {
        const string prefix = "投屏失败：\n";
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return prefix + "未知错误";
        }

        // 卡片画面区很小，截断到开头一段，避免撑爆布局；完整 server 输出在 tooltip 里。
        const int maxChars = 160;
        string body = lastError.Length <= maxChars
            ? lastError
            : lastError.Substring(0, maxChars) + "…";
        return prefix + body;
    }
}
