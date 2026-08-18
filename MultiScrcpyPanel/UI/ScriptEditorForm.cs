using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using MultiScrcpy.Core;
using MultiScrcpy.Core.Scripting;

namespace MultiScrcpy.UI;

/// <summary>
/// 可视化「动作编排器」：用步骤树编辑脚本，保存时自动生成标准 .scr 文本。
/// 也能打开已有 .scr 反向解析成步骤再修改。
/// </summary>
public sealed class ScriptEditorForm : Form
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, FullRowSelect = true, ShowLines = true, ShowPlusMinus = true, ShowRootLines = false };
    private readonly FlowLayoutPanel _props = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10) };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new("就绪");
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripButton _btnNew = new("新建");
    private readonly ToolStripButton _btnOpen = new("打开");
    private readonly ToolStripButton _btnSave = new("保存");
    private readonly ToolStripButton _btnSaveAs = new("另存为");
    private readonly ToolStripDropDownButton _btnAdd = new("添加步骤");
    private readonly ToolStripButton _btnUp = new("上移");
    private readonly ToolStripButton _btnDown = new("下移");
    private readonly ToolStripButton _btnDel = new("删除");
    private readonly ToolStripButton _btnValidate = new("校验");
    private readonly ToolStripButton _btnRefreshTpl = new("刷新模板");

    private readonly List<ScriptStep> _steps = new();
    private readonly string _templatesDir;
    private readonly string _scriptsDir;
    private readonly Action<Action<double, double>>? _captureRequest;
    private string? _currentFile;
    private bool _dirty;

    public ScriptEditorForm(Action<Action<double, double>>? captureRequest = null)
    {
        _templatesDir = ScriptEngine.TemplatesDirectory();
        _scriptsDir = ScriptEngine.DefaultScriptsDirectory();
        _captureRequest = captureRequest;
        Text = "脚本编排器";
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 480);

        BuildToolbar();
        BuildLayout();
        WireEvents();
        RefreshTree();
    }

    // ---- 布局 ----

    private void BuildToolbar()
    {
        _btnNew.Click += (_, _) => NewFile();
        _btnOpen.Click += (_, _) => OpenFile();
        _btnSave.Click += (_, _) => Save(false);
        _btnSaveAs.Click += (_, _) => Save(true);
        _btnUp.Click += (_, _) => MoveStep(-1);
        _btnDown.Click += (_, _) => MoveStep(1);
        _btnDel.Click += (_, _) => DeleteStep();
        _btnValidate.Click += (_, _) => ValidateOnly();
        _btnRefreshTpl.Click += (_, _) => ShowProperties();

        foreach (ScriptStepKind kind in new[]
        {
            ScriptStepKind.Ocr, ScriptStepKind.OcrText, ScriptStepKind.Tap, ScriptStepKind.Swipe,
            ScriptStepKind.Wait, ScriptStepKind.Key, ScriptStepKind.Text, ScriptStepKind.Loop
        })
        {
            var item = new ToolStripMenuItem(kind switch
            {
                ScriptStepKind.Ocr => "OCR 识别点击",
                ScriptStepKind.OcrText => "OCR 文字点击",
                ScriptStepKind.Tap => "坐标点击",
                ScriptStepKind.Swipe => "滑动",
                ScriptStepKind.Wait => "等待",
                ScriptStepKind.Key => "按键",
                ScriptStepKind.Text => "输入文本",
                ScriptStepKind.Loop => "循环开始",
                _ => kind.ToString()
            });
            ScriptStepKind captured = kind;
            item.Click += (_, _) => AddStep(captured);
            _btnAdd.DropDownItems.Add(item);
        }

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _btnNew, _btnOpen, _btnSave, _btnSaveAs, new ToolStripSeparator(),
            _btnAdd, _btnUp, _btnDown, _btnDel, new ToolStripSeparator(), _btnValidate, _btnRefreshTpl
        });
    }

    private void BuildLayout()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 420
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_props);

        _status.Items.Add(_statusLabel);

        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        _toolbar.Visible = true;
        _status.Dock = DockStyle.Bottom;
        _toolbar.Dock = DockStyle.Top;
    }

    private void WireEvents()
    {
        _tree.AfterSelect += (_, _) => ShowProperties();
        _tree.NodeMouseDoubleClick += (_, _) => ShowProperties();
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteStep();
        };
        FormClosing += OnClosing;
    }

    // ---- 步骤树 ----

    private void RefreshTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (ScriptStep s in _steps)
        {
            AddNode(_tree.Nodes, s);
        }

        _tree.EndUpdate();
        MarkDirty(true);
    }

    private void AddNode(TreeNodeCollection parent, ScriptStep s)
    {
        var node = parent.Add(s.Summary);
        node.Tag = s;
        if (s is LoopStep lp)
        {
            node.Text = "▸ " + s.Summary;
            foreach (ScriptStep c in lp.Children)
            {
                AddNode(node.Nodes, c);
            }
        }
    }

    private List<ScriptStep> ParentListOf(TreeNode node)
    {
        return node.Parent == null ? _steps : ((LoopStep)node.Parent.Tag!).Children;
    }

    // ---- 增删改 ----

    private void AddStep(ScriptStepKind kind)
    {
        ScriptStep step = kind switch
        {
            ScriptStepKind.Ocr => new OcrStep(new List<string>(), 0.15, 0, 1, 300, 0, 0, false),
            ScriptStepKind.OcrText => new OcrTextStep("", OcrTextAnchor.Center, 0, 0, 0.2, 0, 1, 300, false),
            ScriptStepKind.Tap => new TapStep(0.5, 0.5, 50, null),
            ScriptStepKind.Swipe => new SwipeStep(0.2, 0.2, 0.8, 0.8, 300),
            ScriptStepKind.Wait => new WaitStep(500),
            ScriptStepKind.Key => new KeyStep("BACK", KeyAction.Press),
            ScriptStepKind.Text => new TextStep(""),
            ScriptStepKind.Loop => new LoopStep(1),
            _ => new WaitStep(500)
        };

        TreeNode? sel = _tree.SelectedNode;
        if (sel?.Tag is LoopStep lp)
        {
            lp.Children.Add(step);
        }
        else
        {
            List<ScriptStep> parent = sel == null ? _steps : ParentListOf(sel);
            int idx = sel == null ? parent.Count : sel.Index + 1;
            parent.Insert(idx, step);
        }

        RefreshTree();
        SelectStep(step);
        ShowProperties();
    }

    private void MoveStep(int dir)
    {
        TreeNode? sel = _tree.SelectedNode;
        if (sel == null || sel.Tag is LoopStep)
        {
            return;
        }

        List<ScriptStep> parent = ParentListOf(sel);
        int idx = sel.Index;
        int j = idx + dir;
        if (j < 0 || j >= parent.Count)
        {
            return;
        }

        (parent[idx], parent[j]) = (parent[j], parent[idx]);
        RefreshTree();
        SelectStep(parent[j]);
    }

    private void DeleteStep()
    {
        TreeNode? sel = _tree.SelectedNode;
        if (sel == null)
        {
            return;
        }

        if (sel.Tag is LoopStep)
        {
            if (MessageBox.Show("循环内还有 " + ((LoopStep)sel.Tag).Children.Count + " 个子步骤，确定删除整个循环？",
                    "删除循环", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
        }

        ParentListOf(sel).Remove((ScriptStep)sel.Tag!);
        RefreshTree();
        ShowProperties();
    }

    private void SelectStep(ScriptStep step)
    {
        foreach (TreeNode n in Enumerate(_tree.Nodes))
        {
            if (ReferenceEquals(n.Tag, step))
            {
                _tree.SelectedNode = n;
                return;
            }
        }
    }

    private static IEnumerable<TreeNode> Enumerate(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (TreeNode c in Enumerate(n.Nodes))
            {
                yield return c;
            }
        }
    }

    // ---- 属性面板 ----

    private void ShowProperties()
    {
        _props.SuspendLayout();
        _props.Controls.Clear();

        ScriptStep? step = _tree.SelectedNode?.Tag as ScriptStep;
        if (step == null)
        {
            _props.Controls.Add(new Label { Text = "在左侧选择或添加一个步骤进行编辑。", AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            _props.ResumeLayout();
            return;
        }

        _props.Controls.Add(new Label { Text = "类型：" + step.Kind, Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });

        switch (step)
        {
            case OcrStep o: BuildOcr(o); break;
            case OcrTextStep o: BuildOcrText(o); break;
            case TapStep t: BuildTap(t); break;
            case SwipeStep s: BuildSwipe(s); break;
            case WaitStep w: BuildWait(w); break;
            case KeyStep k: BuildKey(k); break;
            case TextStep tx: BuildText(tx); break;
            case LoopStep lp: BuildLoop(lp); break;
            case AnchorStep a: BuildAnchor(a); break;
            case RawStep r: BuildRaw(r); break;
        }

        _props.ResumeLayout();
    }

    private void AddRow(Control c, int width = 360)
    {
        c.Width = width;
        c.Margin = new Padding(0, 0, 0, 8);
        _props.Controls.Add(c);
    }

    private Label Label(string text)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
    }

    private NumericUpDown Num(decimal val, decimal min, decimal max, decimal inc)
    {
        return new NumericUpDown
        {
            DecimalPlaces = inc < 1 ? 3 : 0,
            Minimum = min,
            Maximum = max,
            Increment = inc,
            Value = Math.Max(min, Math.Min(max, val))
        };
    }

    private void Bind(NumericUpDown box, Action<double> setter, ScriptStep owner)
    {
        box.ValueChanged += (_, _) => { setter((double)box.Value); UpdateSelectedLabel(owner); };
    }

    /// <summary>OCR 模板匹配器（GDI+ 载入）实际可识别的图片扩展名。</summary>
    private static readonly HashSet<string> OcrImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff"
    };

    /// <summary>
    /// 自动扫描程序目录下的 templates 文件夹，返回全部 OCR 支持的图片（含子文件夹）。
    /// 返回相对路径（以 '/' 分隔，例如 "师门/参加.png"），便于子文件夹组织的模板也能被脚本正确解析。
    /// 列表直接来自磁盘，绝不内定固定集合。
    /// </summary>
    private List<string> LoadTemplateImages()
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(_templatesDir) || !Directory.Exists(_templatesDir))
        {
            return list;
        }

        foreach (string f in Directory.GetFiles(_templatesDir, "*.*", SearchOption.AllDirectories))
        {
            if (OcrImageExts.Contains(Path.GetExtension(f)))
            {
                list.Add(Path.GetRelativePath(_templatesDir, f).Replace('\\', '/'));
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void BuildOcr(OcrStep o)
    {
        AddRow(Label("识别模板图（从 templates 下选择一张；截图命中该图后点击）："));
        List<string> images = LoadTemplateImages();
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        if (images.Count == 0)
        {
            combo.Items.Add(Directory.Exists(_templatesDir)
                ? "(templates 目录暂无 OCR 图片)"
                : "(templates 目录不存在)");
        }
        else
        {
            foreach (string img in images)
            {
                combo.Items.Add(img);
            }

            int sel = o.Images.Count > 0 ? images.IndexOf(o.Images[0]) : 0;
            combo.SelectedIndex = sel >= 0 ? sel : 0;
        }

        combo.SelectedIndexChanged += (_, _) =>
        {
            o.Images.Clear();
            if (combo.SelectedItem is string s && !s.StartsWith("("))
            {
                o.Images.Add(s);
            }

            UpdateSelectedLabel(o);
        };
        AddRow(combo);

        AddRow(Label("点击文字 TEXT（可选）：在模板内识别该文字并精确点击；留空则点击模板命中框。"));
        var txtText = new TextBox { Text = o.Text ?? string.Empty, Width = 360 };
        txtText.TextChanged += (_, _) => { o.Text = txtText.Text.Trim(); UpdateSelectedLabel(o); };
        AddRow(txtText);

        AddRow(Label("容差 MAXERR（越大越宽松，建议 0.10–0.20）："));
        var me = Num((decimal)o.MaxError, 0.01m, 0.5m, 0.01m);
        Bind(me, v => o.MaxError = v, o);
        AddRow(me);

        AddRow(Label("重试次数 RETRY（未命中时重试，0 表示按 TIMEOUT 等待）："));
        var rt = Num(o.Retry, 0, 1000, 1);
        Bind(rt, v => o.Retry = (int)v, o);
        AddRow(rt);

        AddRow(Label("超时 TIMEOUT(ms)（>0 时按时间重试而非次数）："));
        var to = Num(o.TimeoutMs, 0, 600000, 100);
        Bind(to, v => o.TimeoutMs = (int)v, o);
        AddRow(to);

        AddRow(Label("每次未命中后等待 WAIT(ms)："));
        var wt = Num(o.WaitMs, 0, 60000, 50);
        Bind(wt, v => o.WaitMs = (int)v, o);
        AddRow(wt);

        AddRow(Label("点击偏移 DX / DY（相对命中中心，归一化）："));
        var dx = Num((decimal)o.Dx, -0.5m, 0.5m, 0.01m);
        Bind(dx, v => o.Dx = v, o);
        AddRow(dx);
        var dy = Num((decimal)o.Dy, -0.5m, 0.5m, 0.01m);
        Bind(dy, v => o.Dy = v, o);
        AddRow(dy);

        var center = new CheckBox { Text = "点击命中框中心（CENTER）；不勾则在区域内随机取点", AutoSize = true, Checked = o.UseCenter };
        center.CheckedChanged += (_, _) => { o.UseCenter = center.Checked; UpdateSelectedLabel(o); };
        AddRow(center);
    }

    private void BuildTap(TapStep t)
    {
        var anchorMode = new CheckBox { Text = "使用已定义锚点（@名称）", AutoSize = true, Checked = !string.IsNullOrEmpty(t.AnchorName) };
        AddRow(anchorMode);

        var nameBox = new TextBox { Text = t.AnchorName ?? string.Empty, Enabled = anchorMode.Checked };
        AddRow(Label("锚点名称："));
        AddRow(nameBox);

        var x = Num((decimal)t.X, 0m, 2m, 0.001m);
        Bind(x, v => t.X = v, t);
        var y = Num((decimal)t.Y, 0m, 2m, 0.001m);
        Bind(y, v => t.Y = v, t);
        AddRow(Label("坐标 X / Y（归一化 0–1，或 >1 为像素）："));
        AddRow(x);
        AddRow(y);

        if (_captureRequest != null)
        {
            var capBtn = new Button { Text = "录取坐标", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            capBtn.Click += (_, _) =>
            {
                SetStatus("请在设备画面中点击一次以录取坐标…");
                _captureRequest.Invoke((nx, ny) =>
                {
                    this.SafePost(() =>
                    {
                        double cx = Math.Clamp(nx, 0, 1);
                        double cy = Math.Clamp(ny, 0, 1);
                        x.Value = (decimal)cx;
                        y.Value = (decimal)cy;
                        SetStatus($"已录取坐标：X={cx:F3}, Y={cy:F3}");
                    });
                });
            };
            AddRow(capBtn);
        }

        var hold = Num(t.HoldMs, 0, 10000, 10);
        Bind(hold, v => t.HoldMs = (int)v, t);
        AddRow(Label("按住时长(ms)："));
        AddRow(hold);

        anchorMode.CheckedChanged += (_, _) =>
        {
            nameBox.Enabled = anchorMode.Checked;
            x.Enabled = !anchorMode.Checked;
            y.Enabled = !anchorMode.Checked;
            t.AnchorName = anchorMode.Checked ? nameBox.Text.Trim() : null;
            UpdateSelectedLabel(t);
        };
        nameBox.TextChanged += (_, _) => { if (anchorMode.Checked) { t.AnchorName = nameBox.Text.Trim(); UpdateSelectedLabel(t); } };
    }

    private void BuildOcrText(OcrTextStep o)
    {
        AddRow(Label("目标文字（将做真实 OCR 识别）："));
        var textBox = new TextBox { Text = o.Text, Width = 360 };
        textBox.TextChanged += (_, _) => { o.Text = textBox.Text.Trim(); UpdateSelectedLabel(o); };
        AddRow(textBox);

        AddRow(Label("点击锚点（默认文字中心；点击右侧按钮建议选 Right）："));
        var anchor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        anchor.Items.AddRange(new object[] { "CENTER", "LEFT", "RIGHT", "TOP", "BOTTOM" });
        anchor.SelectedItem = o.Anchor.ToString().ToUpperInvariant();
        anchor.SelectedIndexChanged += (_, _) =>
        {
            o.Anchor = Enum.Parse<OcrTextAnchor>((string)anchor.SelectedItem!, true);
            UpdateSelectedLabel(o);
        };
        AddRow(anchor);

        AddRow(Label("相对偏移 DX / DY（归一化；RIGHT 时 DX 为正表示向右）："));
        var dx = Num((decimal)o.Dx, -0.5m, 0.5m, 0.01m);
        Bind(dx, v => o.Dx = v, o);
        var dy = Num((decimal)o.Dy, -0.5m, 0.5m, 0.01m);
        Bind(dy, v => o.Dy = v, o);
        AddRow(dx);
        AddRow(dy);

        AddRow(Label("最大容错 MAXERR（归一化编辑距离，0 = 完全相等，0.5 = 很宽松）："));
        var me = Num((decimal)o.MaxError, 0m, 1m, 0.05m);
        Bind(me, v => o.MaxError = v, o);
        AddRow(me);

        AddRow(Label("重试次数 RETRY："));
        var rt = Num(o.Retry, 0, 1000, 1);
        Bind(rt, v => o.Retry = (int)v, o);
        AddRow(rt);

        AddRow(Label("超时 TIMEOUT(ms)（>0 时按时间重试而非次数）："));
        var to = Num(o.TimeoutMs, 0, 600000, 100);
        Bind(to, v => o.TimeoutMs = (int)v, o);
        AddRow(to);

        AddRow(Label("每次未命中后等待 WAIT(ms)："));
        var wt = Num(o.WaitMs, 0, 60000, 50);
        Bind(wt, v => o.WaitMs = (int)v, o);
        AddRow(wt);

        var caseBox = new CheckBox { Text = "区分大小写（CASE）", AutoSize = true, Checked = o.CaseSensitive };
        caseBox.CheckedChanged += (_, _) => { o.CaseSensitive = caseBox.Checked; UpdateSelectedLabel(o); };
        AddRow(caseBox);
    }

    private void BuildSwipe(SwipeStep s)
    {
        AddRow(Label("起点 X1 / Y1："));
        var x1 = Num((decimal)s.X1, 0m, 2m, 0.001m);
        Bind(x1, v => s.X1 = v, s);
        var y1 = Num((decimal)s.Y1, 0m, 2m, 0.001m);
        Bind(y1, v => s.Y1 = v, s);
        AddRow(x1); AddRow(y1);
        AddRow(Label("终点 X2 / Y2："));
        var x2 = Num((decimal)s.X2, 0m, 2m, 0.001m);
        Bind(x2, v => s.X2 = v, s);
        var y2 = Num((decimal)s.Y2, 0m, 2m, 0.001m);
        Bind(y2, v => s.Y2 = v, s);
        AddRow(x2); AddRow(y2);
        AddRow(Label("时长(ms)："));
        var dur = Num(s.DurationMs, 0, 10000, 10);
        Bind(dur, v => s.DurationMs = (int)v, s);
        AddRow(dur);
    }

    private void BuildWait(WaitStep w)
    {
        AddRow(Label("等待(ms)："));
        var ms = Num(w.Ms, 0, 600000, 50);
        Bind(ms, v => w.Ms = (int)v, w);
        AddRow(ms);
    }

    private void BuildKey(KeyStep k)
    {
        AddRow(Label("按键（keycode 数字或别名：BACK/HOME/ENTER…）："));
        var box = new TextBox { Text = k.Key };
        box.TextChanged += (_, _) => { k.Key = box.Text.Trim(); UpdateSelectedLabel(k); };
        AddRow(box);
        AddRow(Label("动作："));
        var act = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        act.Items.AddRange(new object[] { "PRESS", "DOWN", "UP" });
        act.SelectedItem = k.Action.ToString().ToUpperInvariant();
        act.SelectedIndexChanged += (_, _) => { k.Action = Enum.Parse<KeyAction>((string)act.SelectedItem!, true); UpdateSelectedLabel(k); };
        AddRow(act);
    }

    private void BuildText(TextStep tx)
    {
        AddRow(Label("文本内容："));
        var box = new TextBox { Text = tx.Text, Width = 360, Height = 60, Multiline = true };
        box.TextChanged += (_, _) => { tx.Text = box.Text; UpdateSelectedLabel(tx); };
        AddRow(box);
    }

    private void BuildLoop(LoopStep lp)
    {
        AddRow(Label("循环次数（0 = 无限 ∞）："));
        var cnt = Num(lp.Count, 0, 100000, 1);
        Bind(cnt, v => lp.Count = (int)v, lp);
        AddRow(cnt);
        AddRow(new Label { Text = "提示：先选中本「循环开始」节点，再用顶部「添加步骤」向循环内追加子步骤。", AutoSize = true, ForeColor = Color.Gray });
    }

    private void BuildAnchor(AnchorStep a)
    {
        AddRow(Label("名称："));
        var name = new TextBox { Text = a.Name };
        name.TextChanged += (_, _) => { a.Name = name.Text.Trim(); UpdateSelectedLabel(a); };
        AddRow(name);
        AddRow(Label("X / Y（归一化 0–1）："));
        var x = Num((decimal)a.X, 0m, 1m, 0.001m);
        Bind(x, v => a.X = v, a);
        var y = Num((decimal)a.Y, 0m, 1m, 0.001m);
        Bind(y, v => a.Y = v, a);
        AddRow(x); AddRow(y);
    }

    private void BuildRaw(RawStep r)
    {
        AddRow(new Label { Text = "（无法识别为结构化步骤的原始行，原样保留）：", AutoSize = true, ForeColor = Color.Gray });
        var box = new TextBox { Text = r.Raw, Width = 360, Height = 60, Multiline = true };
        box.TextChanged += (_, _) => { r.Raw = box.Text; UpdateSelectedLabel(r); };
        AddRow(box);
    }

    private void UpdateSelectedLabel(ScriptStep step)
    {
        if (_tree.SelectedNode?.Tag == step)
        {
            _tree.SelectedNode.Text = step is LoopStep ? "▸ " + step.Summary : step.Summary;
        }

        MarkDirty(true);
    }

    // ---- 文件操作 ----

    private void NewFile()
    {
        if (!ConfirmDiscard()) return;
        _steps.Clear();
        _currentFile = null;
        RefreshTree();
        ShowProperties();
        SetStatus("已新建");
        MarkDirty(false);
    }

    private void OpenFile()
    {
        if (!ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "脚本文件 (*.scr)|*.scr|所有文件 (*.*)|*.*",
            Title = "打开脚本",
            InitialDirectory = Directory.Exists(_scriptsDir) ? _scriptsDir : string.Empty
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _steps.Clear();
            _steps.AddRange(ScriptActionModel.BuildSteps(File.ReadAllText(dlg.FileName)));
            _currentFile = dlg.FileName;
            RefreshTree();
            ShowProperties();
            SetStatus("已打开：" + Path.GetFileName(dlg.FileName));
            MarkDirty(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Save(bool askPath)
    {
        string? path = _currentFile;
        if (askPath || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "脚本文件 (*.scr)|*.scr|所有文件 (*.*)|*.*",
                Title = "保存脚本",
                FileName = path == null ? "新脚本.scr" : Path.GetFileName(path),
                InitialDirectory = Directory.Exists(_scriptsDir) ? _scriptsDir : string.Empty
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            path = dlg.FileName;
        }

        string text = ScriptActionModel.ToScript(_steps);
        if (!ScriptEngine.TryParse(text, Path.GetFileName(path), out _, out List<string> errors))
        {
            MessageBox.Show("脚本存在错误，未保存：\n" + string.Join("\n", errors),
                "校验未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            File.WriteAllText(path, text);
            _currentFile = path;
            MarkDirty(false);
            SetStatus("已保存：" + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ValidateOnly()
    {
        string text = ScriptActionModel.ToScript(_steps);
        if (ScriptEngine.TryParse(text, _currentFile ?? "预览", out _, out List<string> errors))
        {
            SetStatus("校验通过，可保存。");
            MessageBox.Show("脚本语法正确，可被引擎执行。", "校验通过", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            SetStatus("校验未通过。");
            MessageBox.Show("校验未通过：\n" + string.Join("\n", errors), "校验未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool ConfirmDiscard()
    {
        // 当前任务为空（无步骤）时无需提示保存，直接放行（新建/打开都不会丢失内容）。
        if (!_dirty || IsScriptEmpty())
        {
            return true;
        }

        return MessageBox.Show("当前脚本有未保存的修改，确定放弃？", "未保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>判断当前任务是否为空（无任何步骤，或仅含空的循环）。</summary>
    private bool IsScriptEmpty()
    {
        foreach (ScriptStep s in _steps)
        {
            if (s is LoopStep lp)
            {
                if (lp.Children.Count > 0)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private void MarkDirty(bool dirty)
    {
        _dirty = dirty;
        string name = _currentFile == null ? "未命名" : Path.GetFileName(_currentFile);
        Text = "脚本编排器 — " + name + (dirty ? " *" : string.Empty);
    }

    private void SetStatus(string msg) => _statusLabel.Text = msg;

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_dirty && MessageBox.Show("当前脚本有未保存的修改，确定关闭？", "未保存", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
        }
    }
}
