using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Scripting;
using MultiScrcpy.Core.Scripting.TextRecognition;

namespace MultiScrcpy.UI;

/// <summary>
/// 脚本控制独立小窗体（从 <see cref="MainForm"/> 顶部工具栏剥离，避免工具栏过于繁杂）。
/// <para>
/// 内含：目标设备选择、脚本选择（.scr，含浏览）、运行 / 停止 / 可视化编排 按钮、运行日志。
/// 运行逻辑原样沿用主窗体实现，状态同时回写到主窗体状态栏（经 <c>onStatus</c>/<c>onError</c> 回调）。
/// OCR 命中高亮经 <c>_cards</c> 按序列号取到对应 <see cref="DeviceCard"/> 后回投到画面层。
/// </para>
/// </summary>
public sealed class ScriptPanelForm : Form
{
    private readonly DeviceManager _manager;
    private readonly IReadOnlyDictionary<string, DeviceCard> _cards;
    private readonly AppConfig _cfg;
    private readonly Action<string>? _onStatus;
    private readonly Action<string>? _onError;

    private readonly ComboBox _deviceBox = new();
    private readonly ComboBox _scriptBox = new();
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _editorButton = new();
    private readonly ListBox _log = new();

    private CancellationTokenSource? _cts;

    /// <summary>当前处于坐标录取模式的设备卡片（用于编辑器关闭时取消未完成录取）。</summary>
    private DeviceCard? _captureCard;

    public ScriptPanelForm(
        DeviceManager manager,
        IReadOnlyDictionary<string, DeviceCard> cards,
        AppConfig? cfg = null,
        Action<string>? onStatus = null,
        Action<string>? onError = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        _cfg = cfg ?? new AppConfig();
        _onStatus = onStatus;
        _onError = onError;

        Text = "脚本控制";
        BackColor = UiTheme.FormBackground;
        MinimumSize = new Size(420, 360);
        ClientSize = new Size(460, 460);
        StartPosition = FormStartPosition.Manual; // 由主窗体以 MainForm.CenterChildOnMain 居中于主窗口
        KeyPreview = true;

        BuildUi();

        // 设备上下线时自动刷新设备下拉
        _manager.DeviceAdded += OnDeviceChanged;
        _manager.DeviceStatusUpdated += OnDeviceChanged;
        _manager.DeviceRemoved += OnDeviceRemoved;
        this.FormClosed += (_, _) =>
        {
            _manager.DeviceAdded -= OnDeviceChanged;
            _manager.DeviceStatusUpdated -= OnDeviceChanged;
            _manager.DeviceRemoved -= OnDeviceRemoved;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };

        RefreshDeviceCombo();
        RefreshScriptCombo();
    }

    // ---- UI 构建 ----

    private void BuildUi()
    {
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 132, Padding = new Padding(8, 8, 8, 4) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            ColumnStyles =
            {
                new ColumnStyle(SizeType.AutoSize),
                new ColumnStyle(SizeType.Percent, 100f)
            },
            RowStyles =
            {
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize),
                new RowStyle(SizeType.AutoSize)
            }
        };

        var deviceLabel = new Label { Text = "设备", AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };
        var scriptLabel = new Label { Text = "脚本", AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft };

        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.Width = 300;
        _deviceBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _deviceBox.DropDown += (_, _) => RefreshDeviceCombo();

        _scriptBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _scriptBox.Width = 300;
        _scriptBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _scriptBox.DropDown += (_, _) => RefreshScriptCombo();
        _scriptBox.SelectedIndexChanged += OnScriptSelected;

        layout.Controls.Add(deviceLabel, 0, 0);
        layout.Controls.Add(_deviceBox, 1, 0);
        layout.Controls.Add(scriptLabel, 0, 1);
        layout.Controls.Add(_scriptBox, 1, 1);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 0)
        };
        _runButton.Text = "运行脚本";
        _runButton.AutoSize = true;
        _runButton.Click += OnRun;
        _stopButton.Text = "停止";
        _stopButton.AutoSize = true;
        _stopButton.Enabled = false;
        _stopButton.Click += OnStop;
        _editorButton.Text = "脚本编辑器";
        _editorButton.AutoSize = true;
        _editorButton.Click += OnOpenEditor;
        btnRow.Controls.AddRange(new Control[] { _runButton, _stopButton, _editorButton });
        layout.Controls.Add(btnRow, 1, 2);

        topPanel.Controls.Add(layout);

        _log.Dock = DockStyle.Fill;
        _log.HorizontalScrollbar = false;
        _log.Font = UiTheme.PlaceholderFont;
        _log.BackColor = UiTheme.FormBackground;
        _log.BorderStyle = BorderStyle.FixedSingle;

        Controls.Add(_log);
        Controls.Add(topPanel);
    }

    // ---- 设备下拉 ----

    private void OnDeviceChanged(DeviceInfo info) => this.SafePost(RefreshDeviceCombo);
    private void OnDeviceRemoved(string serial) => this.SafePost(RefreshDeviceCombo);

    private void RefreshDeviceCombo()
    {
        string? keep = _deviceBox.SelectedItem as string;
        _deviceBox.Items.Clear();
        foreach (DeviceInfo info in _manager.Snapshot())
        {
            DeviceSession? s = _manager.GetSession(info.Serial);
            if (s != null && s.State == DeviceState.Streaming && s.Controller != null && s.Info.VideoWidth > 0)
            {
                _deviceBox.Items.Add(info.Serial);
            }
        }

        if (_deviceBox.Items.Count == 0)
        {
            _deviceBox.Items.Add("(无已投影设备)");
            _deviceBox.SelectedIndex = 0;
        }
        else if (keep != null && _deviceBox.Items.Contains(keep))
        {
            _deviceBox.SelectedItem = keep;
        }
        else
        {
            _deviceBox.SelectedIndex = 0;
        }
    }

    // ---- 脚本下拉 ----

    private void RefreshScriptCombo()
    {
        string? keep = _scriptBox.SelectedItem as string;
        _scriptBox.Items.Clear();
        string dir = ScriptEngine.DefaultScriptsDirectory();
        if (Directory.Exists(dir))
        {
            foreach (string f in Directory.GetFiles(dir, "*.scr", SearchOption.AllDirectories))
            {
                _scriptBox.Items.Add(Path.GetRelativePath(dir, f));
            }
        }

        _scriptBox.Items.Add("浏览…");

        // 若之前选中了某个脚本（含浏览得到的绝对路径），保留选择
        if (keep != null && _scriptBox.Items.Contains(keep))
        {
            _scriptBox.SelectedItem = keep;
        }
    }

    private void OnScriptSelected(object? sender, EventArgs e)
    {
        if (_scriptBox.SelectedItem is string sel && sel == "浏览…")
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "脚本文件 (*.scr)|*.scr|所有文件 (*.*)|*.*",
                Title = "选择脚本文件"
            };

            if (dlg.ShowDialog((IWin32Window?)MainForm.Instance ?? this) == DialogResult.OK)
            {
                if (!_scriptBox.Items.Contains(dlg.FileName))
                {
                    _scriptBox.Items.Insert(_scriptBox.Items.Count - 1, dlg.FileName);
                }
                _scriptBox.SelectedItem = dlg.FileName;
            }
        }
    }

    private string? ResolveScriptPath()
    {
        string? raw = _scriptBox.SelectedItem as string;
        if (string.IsNullOrEmpty(raw))
        {
            raw = _scriptBox.Text.Trim();
        }
        return ScriptEngine.ResolveScriptLocation(raw);
    }

    // ---- 运行 / 停止 ----

    private void OnRun(object? sender, EventArgs e)
    {
        if (_cts != null)
        {
            return;
        }

        string? serial = _deviceBox.SelectedItem as string;
        if (string.IsNullOrEmpty(serial) || serial == "(无已投影设备)")
        {
            ReportError("请先在「设备」下拉中选择一台已投影的设备。");
            return;
        }

        string? scriptPath = ResolveScriptPath();
        if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
        {
            ReportError("请在「脚本」下拉中选择或浏览一个 .scr 脚本文件。");
            return;
        }

        DeviceSession? session = _manager.GetSession(serial);
        if (session?.Controller == null || session.Info.VideoWidth <= 0)
        {
            ReportError($"{serial} 尚未就绪（未投影或无画面）。");
            return;
        }

        if (!ScriptEngine.TryParse(File.ReadAllText(scriptPath), Path.GetFileName(scriptPath),
                out ScriptProgram? program, out List<string> errs))
        {
            ReportError("脚本解析失败：\n" + string.Join("  ", errs));
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        string fileName = Path.GetFileName(scriptPath);
        Log($"运行：{fileName} @ {serial}");
        _onStatus?.Invoke($"脚本运行中：{fileName} @ {serial}");

        DeviceController controller = session.Controller;
        int vw = session.Info.VideoWidth;
        int vh = session.Info.VideoHeight;
        CancellationToken token = _cts.Token;

        // OCR 命中高亮：把匹配框（归一化视频坐标）抛回 UI 线程，叠到对应设备的画面上层
        _cards.TryGetValue(serial, out DeviceCard? ocrCard);
        Action<double, double, double, double>? onOcrHighlight = ocrCard == null
            ? null
            : (x1, y1, x2, y2) => ocrCard.SafePost(() => ocrCard.ShowOcrMarker(x1, y1, x2, y2));

        _ = Task.Run(async () =>
        {
            try
            {
                await ScriptEngine.ExecuteAsync(program!, new DeviceControllerScriptSink(controller, session, vw, vh),
                    vw, vh, token,
                    new Progress<ScriptLogEntry>(entry =>
                        this.SafePost(() => Log($"[{entry.Line}] {entry.Message}"))),
                    onOcrHighlight: onOcrHighlight,
                    textRecognizer: TextRecognizerFactory.Create(_cfg));
                this.SafePost(() =>
                {
                    Log($"完成：{fileName} @ {serial}");
                    _onStatus?.Invoke($"脚本完成：{fileName} @ {serial}");
                });
            }
            catch (OperationCanceledException)
            {
                this.SafePost(() =>
                {
                    Log($"已停止：{fileName} @ {serial}");
                    _onStatus?.Invoke($"脚本已停止：{fileName} @ {serial}");
                });
            }
            catch (ScriptFailStopException ex)
            {
                // OCR ONFAIL STOP：重试耗尽仍未命中导致脚本停止（区别于用户手动取消）。
                this.SafePost(() =>
                {
                    Log($"脚本已停止：{ex.Message} @ {serial}");
                    _onStatus?.Invoke($"脚本已停止：{ex.Message} @ {serial}");
                });
            }
            catch (Exception ex)
            {
                Log("运行出错：" + ex.Message);
                ReportError("脚本运行出错：" + ex.Message);
            }
            finally
            {
                this.SafePost(() => SetRunning(false));
                _cts?.Dispose();
                _cts = null;
            }
        });
    }

    private void OnStop(object? sender, EventArgs e) => _cts?.Cancel();

    private void SetRunning(bool running)
    {
        _runButton.Enabled = !running;
        _stopButton.Enabled = running;
        _deviceBox.Enabled = !running;
        _scriptBox.Enabled = !running;
        _editorButton.Enabled = !running;
    }

    // ---- 可视化编排 -

    private void OnOpenEditor(object? sender, EventArgs e)
    {
        // 让编辑器始终居中于主窗口（而非本脚本面板）。
        var editor = new ScriptEditorForm(captureRequest: CaptureCoordinate)
        {
            Owner = MainForm.Instance
        };
        MainForm.CenterChildOnMain(editor);
        // 关闭后刷新脚本下拉，并取消可能仍在等待的「坐标录取」
        editor.FormClosed += (_, _) =>
        {
            _captureCard?.CancelCoordinateCapture();
            _captureCard = null;
            RefreshScriptCombo();
        };
        editor.Show();
    }

    /// <summary>
    /// 坐标录取协调：以当前「设备」下拉选中的设备为标的，让其画面进入录取模式；
    /// 用户在设备画面上点击一次后，归一化坐标经 <paramref name="onCaptured"/> 回传。
    /// </summary>
    private void CaptureCoordinate(Action<double, double> onCaptured)
    {
        // 先取消上一次可能未完成的录取，避免回调指向已失效的编辑器
        _captureCard?.CancelCoordinateCapture();
        _captureCard = null;

        string? serial = _deviceBox.SelectedItem as string;
        if (string.IsNullOrEmpty(serial) || serial == "(无已投影设备)")
        {
            ReportError("请先在「设备」下拉中选择一台已投影的设备，再录取坐标。");
            return;
        }

        DeviceSession? session = _manager.GetSession(serial);
        if (session == null || session.State != DeviceState.Streaming)
        {
            ReportError($"{serial} 尚未投影，无法录取坐标。");
            return;
        }

        if (!_cards.TryGetValue(serial, out DeviceCard? card))
        {
            ReportError($"未找到设备 {serial} 的卡片。");
            return;
        }

        _captureCard = card;
        card.BeginCoordinateCapture((nx, ny) =>
        {
            _captureCard = null;
            onCaptured(nx, ny);
        });
        Log($"请在「{serial}」的设备画面上点击一次以录取坐标…");
    }

    // ---- 辅助 ----

    private void Log(string message)
    {
        this.SafePost(() =>
        {
            _log.Items.Add(message);
            _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
        });
    }

    private void ReportError(string message)
    {
        Log(message);
        _onError?.Invoke(message);
    }
}
