using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;

namespace MultiScrcpy.UI;

/// <summary>
/// 主窗体（架构文档 §8-T05-5）：工具栏 + 卡片流式网格 + 状态栏。
/// <para>
/// <b>UI 层铁律</b>：本类不直接使用 Socket / Process / FFmpeg，
/// 一切设备操作经 <see cref="DeviceManager"/> / <see cref="DeviceSession"/>；
/// Core 的事件全部在后台线程触发，此处一律通过 <c>SafePost</c> 投递。
/// </para>
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>设备上限提示的匹配关键字（双通道提示判定，PRD v1.2 Q1）。</summary>
    private const string DeviceLimitKeyword = "已达设备上限";

    private static readonly int[] ScaleOptions = { 50, 75, 100, 150, 200 };

    /// <summary>视频码率可选档位（bps），对应 UI 下拉：8 / 20 / 50 / 100 / 200 Mbps。</summary>
    private static readonly int[] BitRateOptions = { 8_000_000, 20_000_000, 50_000_000, 100_000_000, 200_000_000 };

    private readonly AppConfig _cfg;
    private readonly AdbClient _adb;
    private readonly DeviceManager _manager;
    private readonly Dictionary<string, DeviceCard> _cards = new(StringComparer.Ordinal);
    private readonly ToastForm _toast = new();

    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _refreshButton = new("刷新设备");
    private readonly ToolStripButton _reconnectAllButton = new("全部重连");
    private readonly ToolStripComboBox _scaleBox = new();
        private readonly ToolStripComboBox _qualityBox = new();
        private readonly ToolStripComboBox _bitRateBox = new();
        private readonly ToolStripButton _screenshotDirButton = new("截图目录…");
        private readonly ToolStripButton _openScreenshotDirButton = new("打开截图目录");
    private readonly ToolStripButton _scriptWindowButton = new("脚本窗口");
    private readonly ToolStripLabel _summaryLabel = new("在线 0 / 离线 0 / 待授权 0");
    private ScriptPanelForm? _scriptPanel;

    private readonly Panel _content = new();
    private readonly FlowLayoutPanel _flow = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");
    private readonly ToolStripStatusLabel _adbLabel = new();
    private readonly ToolStripStatusLabel _serverLabel = new();
    private readonly ToolStripStatusLabel _ffmpegLabel = new();

    private readonly System.Windows.Forms.Timer _scanTimer = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new();

    private double _scale = 1.0;
    private string _startupError = string.Empty;
    private bool _closingHandled;

    /// <summary>创建主窗体。</summary>
    /// <param name="cfg">全局配置。</param>
    /// <param name="ffmpegVersion">FFmpeg 版本串（由 <c>Program.Main</c> 注册后取得，避免 UI 直连原生库）。</param>
    public MainForm(AppConfig cfg, string ffmpegVersion = "")
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

        // 窗口图标：使用程序集关联图标（由 csproj ApplicationIcon 注入）
        try
        {
            Icon? exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (exeIcon is not null) Icon = exeIcon;
        }
        catch { /* 不影响启动；任务栏图标仍由 ApplicationIcon 提供 */ }

        string adbPath;
        try
        {
            adbPath = _cfg.ResolveAdb();
        }
        catch (AdbException ex)
        {
            // 不再回退成裸名 "adb"：裸名进程启动失败只会抛出晦涩的英文 Win32Exception，
            // 把 ResolveAdb 已经给出的清晰中文指引彻底掩盖。
            // 空路径 → AdbClient.IsAvailable == false，扫描/启动链路会直接给出中文提示。
            adbPath = string.Empty;
            _startupError = ex.Message; // 已是清晰中文指引，OnFormLoaded 会以 Toast + 红字状态栏呈现
            Log.Warn($"adb 未配置：{ex.Message}");
        }

        // 网络/可移动盘自愈：adb 在 Z: 等 WebDAV 盘上时，子进程会 0xC0000006，
        // 自动缓存到本地 %LOCALAPPDATA%\MultiScrcpy\adb 再用。
        adbPath = AdbClient.LocalizeIfRemote(adbPath);

        _adb = new AdbClient(adbPath, _cfg.AdbTimeoutMs);
        _manager = new DeviceManager(_cfg, _adb);

        Text = "多设备投屏控制面板（C# 版）";
        BackColor = UiTheme.FormBackground;
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();
        BuildContent();
        BuildStatusStrip(adbPath, ffmpegVersion);

        Controls.Add(_content);
        Controls.Add(_toolbar);
        Controls.Add(_statusStrip);

        WireManagerEvents();
        ConfigureTimers();

        Load += OnFormLoaded;
        FormClosing += OnFormClosingHandler;
    }

    /// <summary>设备管理器（供集成测试 / 诊断使用）。</summary>
    public DeviceManager Manager => _manager;

    /// <summary>显示一条轻提示（复用单例 Toast，不打断操作）。</summary>
    public void ShowToast(string text, ToastLevel level)
    {
        if (IsDisposed || _toast.IsDisposed)
        {
            return;
        }

        this.SafePost(() => _toast.Show(this, text, level));
    }

    /// <summary>写入状态栏文本。</summary>
    public void SetStatus(string text)
    {
        this.SafePost(() =>
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = SystemColors.ControlText;
        });
    }

    /// <summary>写入状态栏红字（错误）。</summary>
    public void SetErrorStatus(string text)
    {
        this.SafePost(() =>
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = UiTheme.TitleError;
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scanTimer.Stop();
            _statusTimer.Stop();
            _scanTimer.Dispose();
            _statusTimer.Dispose();
            _toast.Dispose();
            _manager.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>构建顶部工具栏。</summary>
    private void BuildToolbar()
    {
        _toolbar.Dock = DockStyle.Top;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.RenderMode = ToolStripRenderMode.System;
        _toolbar.ImageScalingSize = new Size(16, 16);

        _refreshButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _refreshButton.ToolTipText = "立即扫描一次 adb 设备列表";
        _refreshButton.Click += (_, _) => Task.Run(_manager.ScanOnce);

        _reconnectAllButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _reconnectAllButton.ToolTipText = "断开并重新接入所有设备";
        _reconnectAllButton.Click += OnReconnectAll;

        _scaleBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _scaleBox.ToolTipText = "卡片缩放比例";
        foreach (int option in ScaleOptions)
        {
            _scaleBox.Items.Add($"{option}%");
        }

        _scaleBox.SelectedIndex = Array.IndexOf(ScaleOptions, 100);
        _scaleBox.SelectedIndexChanged += OnScaleChanged;

        _qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _qualityBox.ToolTipText = "画面传输质量（码率 / 帧率 / 分辨率）；设置后下次连接或重连生效";
        foreach (VideoQualityPresets preset in VideoQualityPresets.Presets)
        {
            _qualityBox.Items.Add(preset.Name);
        }
        _qualityBox.SelectedIndex = VideoQualityPresets.MatchIndex(_cfg);
        _qualityBox.SelectedIndexChanged += OnQualityChanged;

        _bitRateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _bitRateBox.ToolTipText = "视频码率（Mbps）；设置后下次连接或重连生效。分辨率保持当前值，仅改码率。";
        _bitRateBox.Items.AddRange(BitRateOptions.Select(b => $"{b / 1_000_000} Mbps").ToArray());
        int bitRateIndex = Array.IndexOf(BitRateOptions, _cfg.VideoBitRate);
        _bitRateBox.SelectedIndex = bitRateIndex >= 0 ? bitRateIndex : 0;
        _bitRateBox.SelectedIndexChanged += OnBitRateChanged;

        _screenshotDirButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _screenshotDirButton.ToolTipText = "设置截图保存目录";
        _screenshotDirButton.Click += OnChooseScreenshotDir;

        _openScreenshotDirButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _openScreenshotDirButton.ToolTipText = "在资源管理器中打开截图保存目录";
        _openScreenshotDirButton.Click += OnOpenScreenshotDir;

        _scriptWindowButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _scriptWindowButton.ToolTipText = "打开脚本控制窗口（运行 / 可视化编排脚本）";
        _scriptWindowButton.Click += OnOpenScriptPanel;

        _summaryLabel.Alignment = ToolStripItemAlignment.Right;

        _toolbar.Items.Add(_refreshButton);
        _toolbar.Items.Add(_reconnectAllButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("缩放"));
        _toolbar.Items.Add(_scaleBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("画质"));
        _toolbar.Items.Add(_qualityBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("码率"));
        _toolbar.Items.Add(_bitRateBox);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_screenshotDirButton);
        _toolbar.Items.Add(_openScreenshotDirButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_scriptWindowButton);
        _toolbar.Items.Add(_summaryLabel);
    }

    /// <summary>构建卡片容器（流式布局 + 自动换行 + 滚动）。</summary>
    private void BuildContent()
    {
        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(6);
        _content.BackColor = UiTheme.FormBackground;

        // 偏离说明（D-UI-1）：架构文档写「Panel(AutoScroll) 内套 FlowLayoutPanel(Dock=Fill)」，
        // 但 Dock=Fill 的子控件不会撑开外层 AutoScroll 容器，卡片会被裁切。
        // 因此把 AutoScroll 直接设在 FlowLayoutPanel 上，行为与设计意图一致且滚动可用。
        _flow.Dock = DockStyle.Fill;
        _flow.WrapContents = true;
        _flow.AutoSize = false;
        _flow.AutoScroll = true;
        _flow.FlowDirection = FlowDirection.LeftToRight;
        _flow.BackColor = UiTheme.FormBackground;

        _content.Controls.Add(_flow);
    }

    /// <summary>构建底部状态栏。</summary>
    private void BuildStatusStrip(string adbPath, string ffmpegVersion)
    {
        _statusStrip.Dock = DockStyle.Bottom;
        _statusStrip.SizingGrip = true;

        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        _adbLabel.Text = string.IsNullOrWhiteSpace(adbPath)
            ? "ADB: 未配置（请安装 Android Platform-Tools 或设置 AdbPath）"
            : $"ADB: {adbPath}";
        _serverLabel.Text = $"scrcpy-server v{_cfg.ServerVersion}";
        _ffmpegLabel.Text = string.IsNullOrWhiteSpace(ffmpegVersion) ? "FFmpeg: 未知" : $"FFmpeg: {ffmpegVersion}";

        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel("|"));
        _statusStrip.Items.Add(_adbLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel("|"));
        _statusStrip.Items.Add(_serverLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel("|"));
        _statusStrip.Items.Add(_ffmpegLabel);
    }

    /// <summary>订阅 <see cref="DeviceManager"/> 的后台事件。</summary>
    private void WireManagerEvents()
    {
        _manager.DeviceAdded += info => this.SafePost(() => AddCard(info));
        _manager.DeviceRemoved += serial => this.SafePost(() => RemoveCard(serial));
        _manager.DeviceStatusUpdated += info => this.SafePost(() => RefreshCard(info));
        _manager.ErrorOccurred += (serial, message) => this.SafePost(() => HandleError(serial, message));
        _manager.ScanCompleted += (count, hadError) => this.SafePost(() => OnScanCompleted(count, hadError));
    }

    /// <summary>配置扫描 / 状态轮询定时器。</summary>
    private void ConfigureTimers()
    {
        _scanTimer.Interval = Math.Max(500, _cfg.ScanIntervalMs);
        _scanTimer.Tick += (_, _) => Task.Run(_manager.ScanOnce);

        _statusTimer.Interval = Math.Max(5000, _cfg.StatusIntervalMs);
        _statusTimer.Tick += (_, _) => Task.Run(_manager.PollStatus);
    }

    /// <summary>窗体加载：立即扫描一次并启动定时器。</summary>
    private void OnFormLoaded(object? sender, EventArgs e)
    {
        if (_startupError.Length > 0)
        {
            SetErrorStatus(_startupError);
            ShowToast(_startupError, ToastLevel.Error);
        }
        else
        {
            SetStatus("正在扫描设备…");
        }

        _scanTimer.Start();
        _statusTimer.Start();

        Task.Run(() =>
        {
            _manager.ScanOnce();
            _manager.PollStatus();
        });
    }

    /// <summary>新增卡片；未授权设备只建卡片不建会话。</summary>
    private void AddCard(DeviceInfo info)
    {
        if (_cards.ContainsKey(info.Serial))
        {
            RefreshCard(info);
            return;
        }

        var card = new DeviceCard(info, _manager);
        card.Notify += OnCardNotify;
        card.StatusMessage += SetStatus;
        card.ApplyScale(_scale);

        _cards[info.Serial] = card;
        _flow.Controls.Add(card);

        if (info.State == DeviceState.Unauthorized)
        {
            card.SetUnauthorized(true);
            SetStatus($"{info.Serial}：设备待授权，请在手机上点击「允许」。");
        }
        else
        {
            TryAttach(info, card);
        }

        UpdateSummary();
    }

    /// <summary>移除卡片并断开会话。</summary>
    private void RemoveCard(string serial)
    {
        if (!_cards.TryGetValue(serial, out DeviceCard? card))
        {
            return;
        }

        _cards.Remove(serial);
        card.Notify -= OnCardNotify;
        card.StatusMessage -= SetStatus;
        card.Unbind();

        _flow.Controls.Remove(card);
        card.Dispose();

        Task.Run(() => _manager.Detach(serial));
        SetStatus($"{serial}：设备已移除。");
        UpdateSummary();
    }

    /// <summary>状态更新：刷新标题，必要时补建会话。</summary>
    private void RefreshCard(DeviceInfo info)
    {
        if (!_cards.TryGetValue(info.Serial, out DeviceCard? card))
        {
            AddCard(info);
            return;
        }

        card.UpdateStatus(info);

        if (info.State == DeviceState.Unauthorized)
        {
            card.SetUnauthorized(true);
        }
        else if (!card.IsBound && info.State == DeviceState.Detected)
        {
            TryAttach(info, card);
        }

        UpdateSummary();
    }

    /// <summary>尝试接入设备并绑定卡片。</summary>
    private void TryAttach(DeviceInfo info, DeviceCard card)
    {
        DeviceSession? session = _manager.Attach(info.Serial);
        if (session == null)
        {
            return;
        }

        card.Bind(session);
        SetStatus($"{info.Serial}：正在连接…");
    }

    /// <summary>错误事件统一处理（含设备超限双通道提示）。</summary>
    private void HandleError(string serial, string message)
    {
        string text = string.IsNullOrEmpty(serial) ? message : $"{serial}：{message}";

        if (message.Contains(DeviceLimitKeyword, StringComparison.Ordinal))
        {
            // ⭐ PRD v1.2 Q1：Toast 显眼 + 状态栏可回溯，两者都要。
            ShowToast(message, ToastLevel.Warn);
            SetStatus(message);
            return;
        }

        ShowToast(text, ToastLevel.Error);
        SetErrorStatus(text);

        if (serial.Length > 0 && _cards.TryGetValue(serial, out DeviceCard? card))
        {
            card.Enabled = false;
        }
    }

    /// <summary>卡片提示回调。</summary>
    private void OnCardNotify(string message, ToastLevel level)
    {
        ShowToast(message, level);
    }

    /// <summary>全部重连。</summary>
    private void OnReconnectAll(object? sender, EventArgs e)
    {
        List<string> serials = _cards.Keys.ToList();
        SetStatus($"正在重连 {serials.Count} 台设备…");

        foreach (string serial in serials)
        {
            if (_cards.TryGetValue(serial, out DeviceCard? card))
            {
                card.Unbind();
            }
        }

        Task.Run(() =>
        {
            foreach (string serial in serials)
            {
                _manager.Detach(serial);
            }

            _manager.ScanOnce();

            foreach (DeviceInfo info in _manager.Snapshot())
            {
                if (info.State == DeviceState.Detected)
                {
                    DeviceSession? session = _manager.Attach(info.Serial);
                    if (session == null)
                    {
                        continue;
                    }

                    string serial = info.Serial;
                    this.SafePost(() =>
                    {
                        if (_cards.TryGetValue(serial, out DeviceCard? card))
                        {
                            card.Bind(session);
                        }
                    });
                }
            }

            this.SafePost(() => SetStatus("重连完成。"));
        });
    }

    /// <summary>缩放比例变化。</summary>
    private void OnScaleChanged(object? sender, EventArgs e)
    {
        int index = _scaleBox.SelectedIndex;
        if (index < 0 || index >= ScaleOptions.Length)
        {
            return;
        }

        _scale = ScaleOptions[index] / 100.0;

        _flow.SuspendLayout();
        try
        {
            foreach (DeviceCard card in _cards.Values)
            {
                card.ApplyScale(_scale);
            }
        }
        finally
        {
            _flow.ResumeLayout(true);
        }

        SetStatus($"卡片缩放已设为 {ScaleOptions[index].ToString(CultureInfo.InvariantCulture)}%。");
    }

    /// <summary>画面传输质量（画质预设）变化：写回配置并持久化，下次连接 / 重连生效。</summary>
    private void OnQualityChanged(object? sender, EventArgs e)
    {
        int index = _qualityBox.SelectedIndex;
        if (index < 0 || index >= VideoQualityPresets.Presets.Length)
        {
            return;
        }

        VideoQualityPresets preset = VideoQualityPresets.Presets[index];
        preset.ApplyTo(_cfg);

        try
        {
            _cfg.Save();
            SetStatus($"画质已设为「{preset.Name}」（最长边 {preset.MaxSize}px / 码率 {preset.VideoBitRate / 1_000_000}Mbps / {preset.MaxFps}fps），下次连接或重连生效。");
            ShowToast($"画质已设为「{preset.Name}」", ToastLevel.Info);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败。", ex);
            SetErrorStatus($"保存配置失败：{ex.Message}");
        }
    }

    /// <summary>视频码率变化：写回配置并持久化，下次连接 / 重连生效。仅改码率，不影响分辨率。</summary>
    private void OnBitRateChanged(object? sender, EventArgs e)
    {
        int index = _bitRateBox.SelectedIndex;
        if (index < 0 || index >= BitRateOptions.Length)
        {
            return;
        }

        int bitRate = BitRateOptions[index];
        _cfg.VideoBitRate = bitRate;

        try
        {
            _cfg.Save();
            SetStatus($"视频码率已设为 {bitRate / 1_000_000}Mbps，下次连接或重连生效。当前分辨率（最长边 {_cfg.MaxSize}px）保持不变。");
            ShowToast($"视频码率已设为 {bitRate / 1_000_000}Mbps", ToastLevel.Info);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败。", ex);
            SetErrorStatus($"保存配置失败：{ex.Message}");
        }
    }

    /// <summary>选择截图目录。</summary>
    private void OnChooseScreenshotDir(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择截图保存目录",
            UseDescriptionForTitle = true,
            SelectedPath = _cfg.ScreenshotDir
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _cfg.ScreenshotDir = dialog.SelectedPath;
        try
        {
            _cfg.Save();
            SetStatus($"截图目录已设为：{_cfg.ScreenshotDir}");
            ShowToast("截图目录已更新", ToastLevel.Info);
        }
        catch (Exception ex)
        {
            Log.Error("保存配置失败。", ex);
            SetErrorStatus($"保存配置失败：{ex.Message}");
        }
    }

    /// <summary>在资源管理器中打开截图保存目录（目录不存在时先创建，保证按钮始终可用）。</summary>
    private void OnOpenScreenshotDir(object? sender, EventArgs e)
    {
        string dir = _cfg.ScreenshotDir;
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var psi = new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("打开截图目录失败。", ex);
            ShowToast($"无法打开截图目录：{ex.Message}", ToastLevel.Error);
            SetErrorStatus($"无法打开截图目录：{ex.Message}");
        }
    }

    // ---- 脚本窗口 ----

    /// <summary>打开 / 聚焦脚本控制独立窗口（单实例）；窗口内完成设备选择、脚本运行、可视化编排。</summary>
    private void OnOpenScriptPanel(object? sender, EventArgs e)
    {
        if (_scriptPanel == null || _scriptPanel.IsDisposed)
        {
            _scriptPanel = new ScriptPanelForm(_manager, _cards, _cfg, SetStatus, SetErrorStatus)
            {
                Owner = this,
                StartPosition = FormStartPosition.CenterParent
            };
            _scriptPanel.FormClosed += (_, _) => _scriptPanel = null;
        }

        if (_scriptPanel.WindowState == FormWindowState.Minimized)
        {
            _scriptPanel.WindowState = FormWindowState.Normal;
        }

        _scriptPanel.Show();
        _scriptPanel.BringToFront();
    }

    /// <summary>刷新右上角状态汇总。</summary>
    private void UpdateSummary()
    {
        int online = 0;
        int offline = 0;
        int unauthorized = 0;

        foreach (DeviceInfo info in _manager.Snapshot())
        {
            switch (info.State)
            {
                case DeviceState.Unauthorized:
                    unauthorized++;
                    break;
                case DeviceState.Offline:
                case DeviceState.Error:
                    offline++;
                    break;
                default:
                    online++;
                    break;
            }
        }

        _summaryLabel.Text = $"在线 {online} / 离线 {offline} / 待授权 {unauthorized}";
    }

    /// <summary>扫描结束：把状态栏从「正在扫描」更新为终态文本。</summary>
    /// <param name="count">本轮结束时已知设备数。</param>
    /// <param name="hadError">本轮 adb 调用是否发生异常。</param>
    private void OnScanCompleted(int count, bool hadError)
    {
        if (hadError)
        {
            // 错误已由 ErrorOccurred → HandleError 写入状态栏红字 / Toast，此处不覆盖。
            return;
        }

        SetStatus(count > 0
            ? $"已发现 {count} 台设备。"
            : "未发现设备。请确认：① USB 调试已开启；② 手机已点击「允许 USB 调试」；③ 数据线支持数据传输（非仅充电）。");
    }

    /// <summary>关闭前清理：停定时器 + 关全部会话（保证无残留 adb forward）。</summary>
    private void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
    {
        if (_closingHandled)
        {
            return;
        }

        _closingHandled = true;
        _scanTimer.Stop();
        _statusTimer.Stop();
        _scriptPanel?.Close();

        foreach (DeviceCard card in _cards.Values)
        {
            card.Unbind();
        }

        try
        {
            _manager.ShutdownAll();
        }
        catch (Exception ex)
        {
            Log.Error("关闭全部会话时发生异常。", ex);
        }
    }
}
