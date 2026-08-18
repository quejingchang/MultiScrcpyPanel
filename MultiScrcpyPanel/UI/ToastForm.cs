using System;
using System.Drawing;
using System.Windows.Forms;

namespace MultiScrcpy.UI;

/// <summary>
/// 非模态轻提示窗（架构文档 §8-T05-4）。
/// <para>
/// <b>全局复用单实例</b>：连续触发时复用同一窗口并重置计时器，绝不堆叠；
/// 不抢焦点（<c>WS_EX_NOACTIVATE</c> + <see cref="ShowWithoutActivation"/>）；
/// 3 秒后 300ms 淡出并 <see cref="Form.Hide"/>（不 <c>Dispose</c>，留待复用）。
/// </para>
/// <para><b>全项目禁止 <c>MessageBox</c></b>，唯一例外是 <c>Program.Main</c> 中 FFmpeg 原生库加载失败。</para>
/// </summary>
public sealed class ToastForm : Form
{
    /// <summary>停留时长（毫秒）。</summary>
    public const int HoldMs = 3000;

    /// <summary>淡出总时长（毫秒）。</summary>
    public const int FadeMs = 300;

    /// <summary>淡出定时器间隔（毫秒）。</summary>
    public const int FadeTickMs = 30;

    private const double PeakOpacity = 0.92;
    private const int WsExNoActivate = 0x08000000;
    private const int MaxWidth = 520;

    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _holdTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly double _fadeStep;

    /// <summary>创建（通常由 <see cref="MainForm"/> 持有唯一实例）。</summary>
    public ToastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = UiTheme.ToastInfo;
        Opacity = PeakOpacity;

        _label = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = UiTheme.ToastFont,
            Padding = new Padding(12),
            Location = Point.Empty,
            MaximumSize = new Size(MaxWidth, 0)
        };
        Controls.Add(_label);

        _holdTimer = new System.Windows.Forms.Timer { Interval = HoldMs };
        _holdTimer.Tick += OnHoldElapsed;

        _fadeTimer = new System.Windows.Forms.Timer { Interval = FadeTickMs };
        _fadeTimer.Tick += OnFadeTick;

        _fadeStep = PeakOpacity / Math.Max(1, FadeMs / FadeTickMs);
    }

    /// <summary>不抢焦点。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <inheritdoc />
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate;
            return cp;
        }
    }

    /// <summary>
    /// 显示提示；复用同一实例并重置计时器。
    /// </summary>
    /// <param name="owner">定位参考控件（通常是主窗体）。</param>
    /// <param name="text">提示文本。</param>
    /// <param name="level">提示级别（决定底色）。</param>
    public void Show(Control owner, string text, ToastLevel level)
    {
        if (IsDisposed)
        {
            return;
        }

        _holdTimer.Stop();
        _fadeTimer.Stop();

        _label.Text = string.IsNullOrEmpty(text) ? " " : text;
        BackColor = UiTheme.ToastColorFor(level);

        Size preferred = _label.PreferredSize;
        ClientSize = new Size(Math.Max(120, preferred.Width), Math.Max(36, preferred.Height));
        UiTheme.RoundedRegion(this, 8);

        Reposition(owner);

        Opacity = PeakOpacity;
        if (!Visible)
        {
            base.Show();
        }
        else
        {
            BringToFront();
        }

        _holdTimer.Start();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _holdTimer.Tick -= OnHoldElapsed;
            _fadeTimer.Tick -= OnFadeTick;
            _holdTimer.Stop();
            _fadeTimer.Stop();
            _holdTimer.Dispose();
            _fadeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>定位到 owner 窗口中下部居中；owner 不可用时退回主屏工作区。</summary>
    private void Reposition(Control? owner)
    {
        Rectangle area;
        if (owner is { IsDisposed: false, IsHandleCreated: true } && owner.Width > 0 && owner.Height > 0)
        {
            Point origin = owner.PointToScreen(Point.Empty);
            area = new Rectangle(origin.X, origin.Y, owner.Width, owner.Height);
        }
        else
        {
            area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        }

        int x = area.X + Math.Max(0, (area.Width - Width) / 2);
        int y = area.Y + Math.Max(0, (int)(area.Height * 0.78) - (Height / 2));
        Location = new Point(x, y);
    }

    /// <summary>停留结束 → 开始淡出。</summary>
    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        _fadeTimer.Start();
    }

    /// <summary>淡出一步。</summary>
    private void OnFadeTick(object? sender, EventArgs e)
    {
        double next = Opacity - _fadeStep;
        if (next <= 0.01)
        {
            _fadeTimer.Stop();
            Opacity = PeakOpacity;   // 复位，便于下次直接复用
            Hide();
            return;
        }

        Opacity = next;
    }
}
