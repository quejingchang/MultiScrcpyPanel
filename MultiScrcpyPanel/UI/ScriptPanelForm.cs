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
/// 内含：目标设备选择、脚本选择（.scr，含浏览）、运行列表（一行 = 一个脚本×设备任务）、
/// 运行 / 停止 / 可视化编排 按钮、运行日志。
/// 运行逻辑支持「一次跑多个脚本」：可把当前脚本添加到列表、或一键对全部设备批量添加，
/// 再「全部运行」让所有任务并发执行，每个任务独立进度、可单独停止或重跑。
/// 状态同时回写到主窗体状态栏（经 <c>onStatus</c>/<c>onError</c> 回调）。
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
    private readonly Button _addButton = new();
    private readonly Button _addAllButton = new();
    private readonly DataGridView _grid = new();
    private readonly Button _runAllButton = new();
    private readonly Button _stopAllButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _editorButton = new();
    private readonly ListBox _log = new();

    /// <summary>运行列表：每个任务 = 一个脚本 + 一台设备，可并发执行。</summary>
    private readonly List<RunJob> _jobs = new();

    /// <summary>当前处于坐标录取模式的设备卡片（用于编辑器关闭时取消未完成录取）。</summary>
    private DeviceCard? _captureCard;

    private enum RunJobStatus
    {
        Queued,
        Running,
        Completed,
        Stopped,
        Failed
    }

    private sealed class RunJob
    {
        public string ScriptPath = "";
        public string ScriptName = "";
        public string Serial = "";
        public CancellationTokenSource? Cts;
        public Task? Task;
        public RunJobStatus Status = RunJobStatus.Queued;
        public string Message = "";
        public DataGridViewRow? Row;
    }

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
        MinimumSize = new Size(540, 460);
        ClientSize = new Size(620, 660);
        StartPosition = FormStartPosition.Manual; // 由主窗体以 MainForm.CenterChildOnMain 居中于主窗口

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
            StopAll();
            _captureCard = null;
        };

        RefreshDeviceCombo();
        RefreshScriptCombo();
        UpdateControls();
    }

    // ---- UI 构建 ----

    private void BuildUi()
    {
        // 顶部：设备 / 脚本 选择 + 添加按钮
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(8, 8, 8, 4) };
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
        _addButton.Text = "添加任务";
        _addButton.AutoSize = true;
        _addButton.Click += OnAdd;
        _addAllButton.Text = "添加全部设备";
        _addAllButton.AutoSize = true;
        _addAllButton.Click += OnAddAll;
        btnRow.Controls.AddRange(new Control[] { _addButton, _addAllButton });
        layout.Controls.Add(btnRow, 1, 2);

        topPanel.Controls.Add(layout);

        // 中部：运行列表（DataGridView）+ 日志，用 SplitContainer 上下分隔
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = UiTheme.FormBackground
        };
        split.SplitterDistance = (int)(ClientSize.Height * 0.50);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = UiTheme.FormBackground;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.ScrollBars = ScrollBars.Vertical;
        _grid.Font = UiTheme.PlaceholderFont;
        _grid.CellContentClick += OnGridCellClick;
        BuildGridColumns();
        split.Panel1.Controls.Add(_grid);

        _log.Dock = DockStyle.Fill;
        _log.HorizontalScrollbar = false;
        _log.Font = UiTheme.PlaceholderFont;
        _log.BackColor = UiTheme.FormBackground;
        _log.BorderStyle = BorderStyle.FixedSingle;
        split.Panel2.Controls.Add(_log);

        // 底部：运行控制 + 编辑器
        var actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6) };
        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0)
        };
        _runAllButton.Text = "全部运行";
        _runAllButton.AutoSize = true;
        _runAllButton.Click += OnRunAll;
        _stopAllButton.Text = "全部停止";
        _stopAllButton.AutoSize = true;
        _stopAllButton.Click += OnStopAll;
        _clearButton.Text = "清空已完成";
        _clearButton.AutoSize = true;
        _clearButton.Click += OnClear;
        _editorButton.Text = "脚本编辑器";
        _editorButton.AutoSize = true;
        _editorButton.Click += OnOpenEditor;
        actionRow.Controls.AddRange(new Control[] { _runAllButton, _stopAllButton, _clearButton, _editorButton });
        actionPanel.Controls.Add(actionRow);

        // 添加顺序：先 Fill（split），再 Top / Bottom 边角
        Controls.Add(split);
        Controls.Add(topPanel);
        Controls.Add(actionPanel);
    }

    private void BuildGridColumns()
    {
        var scriptCol = new DataGridViewTextBoxColumn
        {
            Name = "Script",
            HeaderText = "脚本",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100f,
            ReadOnly = true
        };
        var deviceCol = new DataGridViewTextBoxColumn
        {
            Name = "Device",
            HeaderText = "设备",
            Width = 140,
            ReadOnly = true
        };
        var statusCol = new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "状态",
            Width = 72,
            ReadOnly = true
        };
        var opCol = new DataGridViewButtonColumn
        {
            Name = "Op",
            HeaderText = "操作",
            Width = 72,
            UseColumnTextForButtonValue = false,
            Text = "运行"
        };
        _grid.Columns.AddRange(scriptCol, deviceCol, statusCol, opCol);
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

    // ---- 运行列表：添加任务 ----

    private void OnAdd(object? sender, EventArgs e)
    {
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

        AddJob(serial, scriptPath);
        UpdateControls();
    }

    private void OnAddAll(object? sender, EventArgs e)
    {
        string? scriptPath = ResolveScriptPath();
        if (string.IsNullOrEmpty(scriptPath) || !File.Exists(scriptPath))
        {
            ReportError("请在「脚本」下拉中选择或浏览一个 .scr 脚本文件，再批量添加到全部设备。");
            return;
        }

        int added = 0;
        foreach (DeviceInfo info in _manager.Snapshot())
        {
            DeviceSession? s = _manager.GetSession(info.Serial);
            if (s != null && s.State == DeviceState.Streaming && s.Controller != null && s.Info.VideoWidth > 0)
            {
                AddJob(info.Serial, scriptPath);
                added++;
            }
        }

        if (added == 0)
        {
            ReportError("当前没有已投影且就绪的设备，无法批量添加。");
        }
        else
        {
            Log($"已为「{Path.GetFileName(scriptPath)}」批量添加 {added} 个设备任务。");
        }

        UpdateControls();
    }

    private void AddJob(string serial, string scriptPath)
    {
        var job = new RunJob
        {
            ScriptPath = scriptPath,
            ScriptName = Path.GetFileName(scriptPath),
            Serial = serial,
            Status = RunJobStatus.Queued
        };

        var row = new DataGridViewRow();
        row.CreateCells(_grid, job.ScriptName, job.Serial, StatusText(job.Status), "运行");
        row.Tag = job;
        job.Row = row;
        _grid.Rows.Add(row);
        _jobs.Add(job);

        Log($"已加入任务：{job.ScriptName} @ {serial}");
    }

    // ---- 运行列表：执行 / 停止 ----

    private void OnRunAll(object? sender, EventArgs e)
    {
        int started = 0;
        foreach (RunJob job in _jobs)
        {
            if (job.Status == RunJobStatus.Queued)
            {
                StartJob(job);
                started++;
            }
        }

        if (started == 0)
        {
            Log("没有排队中的任务（可先「添加任务」或「添加全部设备」）。");
        }
        else
        {
            Log($"已开始并发运行 {started} 个任务。");
        }

        UpdateControls();
    }

    private void OnStopAll(object? sender, EventArgs e)
    {
        int stopped = 0;
        foreach (RunJob job in _jobs)
        {
            if (job.Status == RunJobStatus.Running)
            {
                job.Cts?.Cancel();
                stopped++;
            }
        }

        Log(stopped > 0 ? $"已请求停止 {stopped} 个运行中的任务。" : "当前没有运行中的任务。");
        UpdateControls();
    }

    private void OnClear(object? sender, EventArgs e)
    {
        var finished = _jobs.Where(j => j.Status != RunJobStatus.Running).ToList();
        if (finished.Count == 0)
        {
            Log("没有可清空的已结束任务（运行中的任务会保留）。");
            return;
        }

        foreach (RunJob job in finished)
        {
            if (job.Row != null && !job.Row.IsNewRow)
            {
                _grid.Rows.Remove(job.Row);
            }
        }
        _jobs.RemoveAll(j => j.Status != RunJobStatus.Running);
        Log($"已清空 {finished.Count} 个已结束任务。");
        UpdateControls();
    }

    private void OnGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }
        if (_grid.Columns[e.ColumnIndex].Name != "Op")
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].Tag is not RunJob job)
        {
            return;
        }

        if (job.Status == RunJobStatus.Running)
        {
            job.Cts?.Cancel();
            Log($"已请求停止：{job.ScriptName} @ {job.Serial}");
        }
        else
        {
            StartJob(job);
        }

        UpdateControls();
    }

    /// <summary>启动单个任务：独立 CancellationTokenSource + 独立 Task，与其他任务并发执行。</summary>
    private void StartJob(RunJob job)
    {
        if (job.Status == RunJobStatus.Running)
        {
            return;
        }

        // 设备就绪性检查
        DeviceSession? session = _manager.GetSession(job.Serial);
        if (session?.Controller == null || session.Info.VideoWidth <= 0)
        {
            job.Status = RunJobStatus.Failed;
            job.Message = "设备未就绪（未投影或无画面）";
            RefreshRow(job);
            Log($"任务失败（未就绪）：{job.ScriptName} @ {job.Serial}");
            UpdateControls();
            return;
        }

        // 脚本解析
        if (!ScriptEngine.TryParse(File.ReadAllText(job.ScriptPath), job.ScriptName,
                out ScriptProgram? program, out List<string> errs))
        {
            job.Status = RunJobStatus.Failed;
            job.Message = "脚本解析失败：" + string.Join("  ", errs);
            RefreshRow(job);
            Log($"任务失败（解析）：{job.ScriptName} @ {job.Serial}");
            UpdateControls();
            return;
        }

        job.Cts = new CancellationTokenSource();
        job.Status = RunJobStatus.Running;
        job.Message = "";
        RefreshRow(job);

        string fileName = job.ScriptName;
        string serial = job.Serial;
        Log($"运行：{fileName} @ {serial}");
        _onStatus?.Invoke($"脚本运行中：{fileName} @ {serial}");

        DeviceController controller = session.Controller;
        int vw = session.Info.VideoWidth;
        int vh = session.Info.VideoHeight;
        CancellationToken token = job.Cts.Token;

        // OCR 命中高亮：按本任务设备序列号路由到对应卡片
        _cards.TryGetValue(serial, out DeviceCard? ocrCard);
        Action<double, double, double, double>? onOcrHighlight = ocrCard == null
            ? null
            : (x1, y1, x2, y2) => ocrCard.SafePost(() => ocrCard.ShowOcrMarker(x1, y1, x2, y2));

        job.Task = Task.Run(async () =>
        {
            RunJobStatus outcome = RunJobStatus.Completed;
            try
            {
                await ScriptEngine.ExecuteAsync(program!, new DeviceControllerScriptSink(controller, session, vw, vh),
                    vw, vh, token,
                    new Progress<ScriptLogEntry>(entry =>
                        this.SafePost(() => Log($"[{serial}] [{entry.Line}] {entry.Message}"))),
                    onOcrHighlight: onOcrHighlight,
                    textRecognizer: TextRecognizerFactory.Create(_cfg));
            }
            catch (OperationCanceledException)
            {
                outcome = RunJobStatus.Stopped;
            }
            catch (ScriptFailStopException ex)
            {
                // OCR ONFAIL STOP：重试耗尽仍未命中导致脚本停止（区别于用户手动取消）。
                outcome = RunJobStatus.Stopped;
                job.Message = ex.Message;
            }
            catch (Exception ex)
            {
                outcome = RunJobStatus.Failed;
                job.Message = ex.Message;
            }
            finally
            {
                this.SafePost(() =>
                {
                    job.Status = outcome;
                    job.Cts?.Dispose();
                    job.Cts = null;
                    RefreshRow(job);
                    Log(outcome == RunJobStatus.Completed
                        ? $"完成：{fileName} @ {serial}"
                        : outcome == RunJobStatus.Stopped
                            ? $"已停止：{fileName} @ {serial}" + (string.IsNullOrEmpty(job.Message) ? "" : $"（{job.Message}）")
                            : $"失败：{fileName} @ {serial} - {job.Message}");
                    UpdateControls();
                });
            }
        });
    }

    /// <summary>停止全部运行中的任务。</summary>
    private void StopAll()
    {
        foreach (RunJob job in _jobs)
        {
            if (job.Status == RunJobStatus.Running)
            {
                try
                {
                    job.Cts?.Cancel();
                }
                catch
                {
                    // 忽略取消过程中的异常
                }
            }
        }
    }

    // ---- 行刷新 / 控件状态 ----

    private static string StatusText(RunJobStatus status) => status switch
    {
        RunJobStatus.Queued => "排队中",
        RunJobStatus.Running => "运行中",
        RunJobStatus.Completed => "已完成",
        RunJobStatus.Stopped => "已停止",
        RunJobStatus.Failed => "失败",
        _ => "-"
    };

    private void RefreshRow(RunJob job)
    {
        if (job.Row == null || job.Row.IsNewRow)
        {
            return;
        }

        this.SafePost(() =>
        {
            job.Row.Cells[0].Value = job.ScriptName;
            job.Row.Cells[1].Value = job.Serial;
            job.Row.Cells[2].Value = StatusText(job.Status);
            job.Row.Cells[3].Value = job.Status == RunJobStatus.Running ? "停止" : "运行";
        });
    }

    private void UpdateControls()
    {
        int queued = _jobs.Count(j => j.Status == RunJobStatus.Queued);
        int running = _jobs.Count(j => j.Status == RunJobStatus.Running);
        int finished = _jobs.Count(j => j.Status != RunJobStatus.Running);

        _runAllButton.Enabled = queued > 0;
        _stopAllButton.Enabled = running > 0;
        _clearButton.Enabled = finished > 0;

        if (running > 0)
        {
            _onStatus?.Invoke($"脚本运行中：{running} 个任务");
        }
        else if (_jobs.Count == 0)
        {
            _onStatus?.Invoke("脚本空闲");
        }
        else
        {
            _onStatus?.Invoke($"脚本就绪：{_jobs.Count} 个任务");
        }
    }

    // ---- 可视化编排 ----

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
