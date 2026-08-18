using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using MultiScrcpy.Core;

namespace MultiScrcpy.UI;

/// <summary>Toast 提示级别。</summary>
public enum ToastLevel
{
    /// <summary>普通信息（深灰底）。</summary>
    Info = 0,

    /// <summary>警告（橙底）。</summary>
    Warn = 1,

    /// <summary>错误（红底）。</summary>
    Error = 2
}

/// <summary>
/// UI 主题常量与通用扩展（架构文档 §8-T05-1）。
/// </summary>
public static class UiTheme
{
    /// <summary>在线（投影中 / 已识别）标题底色 <c>#2E7D32</c>。</summary>
    public static readonly Color TitleOnline = Color.FromArgb(0x2E, 0x7D, 0x32);

    /// <summary>离线标题底色 <c>#757575</c>。</summary>
    public static readonly Color TitleOffline = Color.FromArgb(0x75, 0x75, 0x75);

    /// <summary>待授权标题底色 <c>#EF6C00</c>（橙）。</summary>
    public static readonly Color TitleUnauthorized = Color.FromArgb(0xEF, 0x6C, 0x00);

    /// <summary>低电量标题底色 <c>#C62828</c>（<c>Battery &lt; 20</c>）。</summary>
    public static readonly Color TitleLowBattery = Color.FromArgb(0xC6, 0x28, 0x28);

    /// <summary>错误标题底色 <c>#B71C1C</c>。</summary>
    public static readonly Color TitleError = Color.FromArgb(0xB7, 0x1C, 0x1C);

    /// <summary>Toast 信息底色。</summary>
    public static readonly Color ToastInfo = Color.FromArgb(255, 66, 66, 66);

    /// <summary>Toast 警告底色。</summary>
    public static readonly Color ToastWarn = Color.FromArgb(255, 239, 108, 0);

    /// <summary>Toast 错误底色。</summary>
    public static readonly Color ToastError = Color.FromArgb(255, 198, 40, 40);

    /// <summary>画面区背景（深灰）。</summary>
    public static readonly Color ScreenBackground = Color.FromArgb(0x1E, 0x1E, 0x1E);

    /// <summary>画面区 letterbox 黑边。</summary>
    public static readonly Color ScreenLetterbox = Color.Black;

    /// <summary>占位文字颜色。</summary>
    public static readonly Color PlaceholderText = Color.FromArgb(0xBD, 0xBD, 0xBD);

    /// <summary>卡片边框色。</summary>
    public static readonly Color CardBorder = Color.FromArgb(0xCF, 0xD8, 0xDC);

    /// <summary>主窗体背景。</summary>
    public static readonly Color FormBackground = Color.FromArgb(0xF5, 0xF5, 0xF5);

    /// <summary>标题栏文字色。</summary>
    public static readonly Color TitleForeground = Color.White;

    /// <summary>标题栏字体。</summary>
    public static readonly Font TitleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);

    /// <summary>按钮字体。</summary>
    public static readonly Font ButtonFont = new("Microsoft YaHei UI", 9F, FontStyle.Regular);

    /// <summary>占位文字字体。</summary>
    public static readonly Font PlaceholderFont = new("Microsoft YaHei UI", 10F, FontStyle.Regular);

    /// <summary>Toast 字体。</summary>
    public static readonly Font ToastFont = new("Microsoft YaHei UI", 10F, FontStyle.Regular);

    /// <summary>低电量阈值（百分比）。</summary>
    public const int LowBatteryThreshold = 20;

    /// <summary>
    /// 安全地把动作投递到控件所属的 UI 线程（架构文档 §1.5）。
    /// <para>
    /// 一律使用 <c>BeginInvoke</c>（<b>禁止 <c>Invoke</c></b>，会与后台线程互等造成死锁）；
    /// 控件已释放 / 句柄未创建时静默丢弃。
    /// </para>
    /// </summary>
    public static void SafePost(this Control? control, Action? action)
    {
        if (control == null || action == null)
        {
            return;
        }

        try
        {
            if (control.IsDisposed || control.Disposing)
            {
                return;
            }

            if (!control.InvokeRequired)
            {
                action();
                return;
            }

            if (!control.IsHandleCreated)
            {
                return;
            }

            control.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 控件已在投递途中释放，忽略。
        }
        catch (InvalidOperationException)
        {
            // 句柄尚未创建 / 已销毁，忽略。
        }
    }

    /// <summary>给控件设置圆角 <see cref="Region"/>。</summary>
    public static void RoundedRegion(Control control, int radius)
    {
        ArgumentNullException.ThrowIfNull(control);

        int w = Math.Max(control.Width, 1);
        int h = Math.Max(control.Height, 1);
        int r = Math.Max(1, Math.Min(radius, Math.Min(w, h) / 2));
        int d = r * 2;

        using var path = new GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d - 1, 0, d, d, 270, 90);
        path.AddArc(w - d - 1, h - d - 1, d, d, 0, 90);
        path.AddArc(0, h - d - 1, d, d, 90, 90);
        path.CloseFigure();

        control.Region?.Dispose();
        control.Region = new Region(path);
    }

    /// <summary>按设备状态 / 电量选择标题栏底色。</summary>
    public static Color TitleColorFor(DeviceInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return info.State switch
        {
            DeviceState.Unauthorized => TitleUnauthorized,
            DeviceState.Error => TitleError,
            DeviceState.Offline => TitleOffline,
            _ => info.IsLowBattery() ? TitleLowBattery : TitleOnline
        };
    }

    /// <summary>按 Toast 级别选择底色。</summary>
    public static Color ToastColorFor(ToastLevel level) => level switch
    {
        ToastLevel.Warn => ToastWarn,
        ToastLevel.Error => ToastError,
        _ => ToastInfo
    };

    /// <summary>创建统一风格的控制按钮。</summary>
    public static Button CreateActionButton(string text, string tooltipText, ToolTip tips)
    {
        ArgumentNullException.ThrowIfNull(tips);

        var button = new Button
        {
            Text = text,
            Font = ButtonFont,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            MinimumSize = new Size(0, 28),
            FlatStyle = FlatStyle.System,
            UseVisualStyleBackColor = true,
            TabStop = false
        };

        tips.SetToolTip(button, tooltipText);
        return button;
    }
}
