using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Protocol;

namespace MultiScrcpy.UI;

/// <summary>
/// 单台设备的画面显示与鼠标交互控件（架构文档 §8-T05-2）。
/// <para>
/// 只依赖 <see cref="FrameBuffer"/>（取帧）与 <see cref="CoordinateMapper"/>（坐标换算），
/// <b>不直接接触 socket / FFmpeg</b>；所有输入通过 <see cref="Touched"/> / <see cref="Scrolled"/>
/// 事件上抛给 <see cref="DeviceCard"/>。
/// </para>
/// </summary>
public sealed class ScreenView : Control
{
    private const string DefaultPlaceholder = "等待画面…";

    private FrameBuffer? _frames;
    private int _videoW;
    private int _videoH;
    private bool _dragging;
    private string _placeholder = DefaultPlaceholder;
    private bool _interactive = true;

    /// <summary>坐标录取模式：下一次左键点击只回传归一化坐标，不注入设备。</summary>
    private bool _captureMode;

    /// <summary>坐标录取事件：用户点击画面后回传归一化视频坐标 (nx, ny ∈ [0,1])。</summary>
    public event Action<double, double>? CoordinateCaptured;

    // ⭐ OCR / FIND 命中标记：归一化视频坐标矩形（x1,y1,x2,y2 ∈ 0–1）叠加在画面上层，
    //    持续 OcrMarkerMs 后自动消失。支持同时显示多个（多图标各自命中、交集分别高亮）。
    private readonly List<(RectangleF Rect, DateTime Expire)> _ocrMarkers = new();
    private readonly System.Windows.Forms.Timer _ocrTimer = new() { Interval = 80 };
    private const int OcrMarkerMs = 1000;

    /// <summary>创建画面控件。</summary>
    public ScreenView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);

        // ⭐ 下限必须低于所有档位下最小的卡片画面区，否则控件会被强行撑大后再被父容器裁切，
        //    letterbox 按撑大后的尺寸计算 → 下发的目标尺寸大于可见区域，画面溢出且被二次缩放。
        //    竖屏 50% 档画面区 158x188；横屏 50% 档画面区可低至约 254x107（宽扁形）。
        //    原先的 120x160 在横屏下会把高度顶到 160，重新制造出上下黑边并压掉按键区，
        //    因此收敛到一个两方向都不会触发的极小值。
        MinimumSize = new Size(48, 48);
        BackColor = UiTheme.ScreenBackground;
        TabStop = false;

        _ocrTimer.Tick += (_, _) => OcrTimerTick();
    }

    /// <summary>触摸事件（action, videoX, videoY, videoW, videoH）。</summary>
    public event Action<byte, int, int, int, int>? Touched;

    /// <summary>滚轮事件（videoX, videoY, videoW, videoH, hScroll, vScroll）。</summary>
    public event Action<int, int, int, int, float, float>? Scrolled;

    /// <summary>画面区 letterbox 尺寸变化（宽, 高），宿主据此下发 <c>SetTargetSize</c>。</summary>
    public event Action<int, int>? LetterboxChanged;

    /// <summary>当前视频宽度。</summary>
    public int VideoWidth => _videoW;

    /// <summary>当前视频高度。</summary>
    public int VideoHeight => _videoH;

    /// <summary>绑定帧源与视频分辨率。</summary>
    public void SetSource(FrameBuffer? frames, int videoW, int videoH)
    {
        _frames = frames;
        _videoW = videoW;
        _videoH = videoH;
        _placeholder = DefaultPlaceholder;
        _interactive = frames != null && videoW > 0 && videoH > 0;
        RaiseLetterboxChanged();
        Invalidate();
    }

    /// <summary>
    /// 显示引导 / 占位文案（例如未授权提示）；此状态下<b>不响应鼠标</b>。
    /// </summary>
    public void SetPlaceholder(string text)
    {
        _placeholder = string.IsNullOrWhiteSpace(text) ? DefaultPlaceholder : text;
        _frames = null;
        _interactive = false;
        _dragging = false;
        Invalidate();
    }

    /// <summary>清除占位文案，恢复交互（帧源仍需另行 <see cref="SetSource"/>）。</summary>
    public void ClearPlaceholder()
    {
        _placeholder = DefaultPlaceholder;
        _interactive = _frames != null && _videoW > 0 && _videoH > 0;
        Invalidate();
    }

    /// <summary>进入坐标录取模式：下一次左键点击画面将回传归一化坐标而不注入设备。</summary>
    public void BeginCoordinateCapture()
    {
        _captureMode = true;
        Cursor = Cursors.Cross;
    }

    /// <summary>退出坐标录取模式（用户取消或已录取）。</summary>
    public void CancelCoordinateCapture()
    {
        _captureMode = false;
        Cursor = Cursors.Default;
    }

    /// <summary>
    /// 当前画面区 letterbox 矩形；<b>分辨率未知时返回 <see cref="Rectangle.Empty"/></b>。
    /// <para>
    /// ⭐ 旧实现在 <c>_videoW/_videoH &lt;= 0</c> 时返回整个控件矩形，宿主会据此下发一个
    /// <b>长宽比完全错误</b>的目标尺寸（控件是接近正方的画面区，而设备通常是 9:20），
    /// 首帧到达前渲染管线就按错误尺寸建好了位图，画面被拉伸后再缩回 → 模糊。
    /// 返回 Empty 后，<see cref="RaiseLetterboxChanged"/> 的 <c>r.Width &lt;= 0</c>
    /// 守卫会跳过下发，等真实分辨率到达再算。
    /// </para>
    /// </summary>
    public Rectangle CurrentLetterbox()
    {
        if (_videoW <= 0 || _videoH <= 0)
        {
            return Rectangle.Empty;
        }

        return CoordinateMapper.ComputeLetterbox(Width, Height, _videoW, _videoH);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(UiTheme.ScreenLetterbox);

        Bitmap? bmp = _frames?.Acquire();
        if (bmp == null || _videoW <= 0 || _videoH <= 0)
        {
            DrawPlaceholder(g);
            return;
        }

        Rectangle r = CoordinateMapper.ComputeLetterbox(Width, Height, _videoW, _videoH);
        if (r.Width <= 0 || r.Height <= 0)
        {
            DrawPlaceholder(g);
            return;
        }

        try
        {
            if (bmp.Width == r.Width && bmp.Height == r.Height)
            {
                // ⭐ 常态路径：DeviceSession 已按精确 letterbox 尺寸出帧，这里是 1:1 像素拷贝。
                //    用最近邻 + HighSpeed 偏移，彻底避免 GDI+ 的第二次重采样（模糊的主因）。
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bmp, r);
                DrawOcrMarker(g, r);
                return;
            }

            // 过渡窗口（拖窗 / 旋转后目标尺寸尚未生效）：仍需缩放，选高质量插值兜底。
            g.InterpolationMode = bmp.Width > r.Width
                ? InterpolationMode.HighQualityBilinear    // 缩小
                : InterpolationMode.HighQualityBicubic;    // 放大
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;

            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);        // 消除高质量插值在边缘取样越界产生的暗边
            g.DrawImage(bmp, r, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            DrawOcrMarker(g, r);
        }
        catch (ArgumentException)
        {
            // 旋转瞬间位图可能正在被替换，跳过本帧即可。
            DrawPlaceholder(g);
        }
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RaiseLetterboxChanged();
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_interactive || e.Button != MouseButtons.Left)
        {
            return;
        }

        if (!TryMap(e.X, e.Y, out int vx, out int vy))
        {
            return;
        }

        // ⭐ 坐标录取模式：本次点击不注入到设备，只把归一化坐标回传给调用方。
        if (_captureMode)
        {
            _captureMode = false;
            Cursor = Cursors.Default;
            double nx = _videoW > 0 ? (double)vx / _videoW : 0;
            double ny = _videoH > 0 ? (double)vy / _videoH : 0;
            CoordinateCaptured?.Invoke(nx, ny);
            return;
        }

        _dragging = true;
        Capture = true;
        Touched?.Invoke(ScrcpyConstants.ACTION_DOWN, vx, vy, _videoW, _videoH);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_interactive || !_dragging)
        {
            return;
        }

        if (!TryMap(e.X, e.Y, out int vx, out int vy))
        {
            return;
        }

        Touched?.Invoke(ScrcpyConstants.ACTION_MOVE, vx, vy, _videoW, _videoH);
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = false;
        Capture = false;

        if (!_interactive || !TryMap(e.X, e.Y, out int vx, out int vy))
        {
            return;
        }

        Touched?.Invoke(ScrcpyConstants.ACTION_UP, vx, vy, _videoW, _videoH);
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // 拖拽中鼠标离开控件：由 Capture 保证仍能收到 MouseUp，这里无需处理。
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_interactive)
        {
            return;
        }

        if (!TryMap(e.X, e.Y, out int vx, out int vy))
        {
            return;
        }

        Scrolled?.Invoke(vx, vy, _videoW, _videoH, 0f, e.Delta / 120f);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frames = null;
            _ocrTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 在画面上叠加一个 OCR / FIND 命中标记（归一化视频坐标 nx∈[0,1]），约 1 秒后自动消失。
    /// 支持同时叠加多个（如多图标各自命中、再叠加交集），并立即同步重绘一次以保证命中瞬间可见。
    /// 应在 UI 线程调用（通常由 <see cref="UiTheme.SafePost"/> 投递）。
    /// </summary>
    public void ShowOcrMarker(double nx1, double ny1, double nx2, double ny2)
    {
        if (_videoW <= 0 || _videoH <= 0)
        {
            return;
        }

        var rect = new RectangleF(
            (float)Math.Min(nx1, nx2),
            (float)Math.Min(ny1, ny2),
            (float)Math.Abs(nx2 - nx1),
            (float)Math.Abs(ny2 - ny1));
        if (rect.Width < 0.0005f || rect.Height < 0.0005f)
        {
            return;
        }

        lock (_ocrMarkers)
        {
            _ocrMarkers.Add((rect, DateTime.UtcNow.AddMilliseconds(OcrMarkerMs)));
        }

        _ocrTimer.Enabled = true;
        // Refresh() 立即同步重绘一次，确保标记在命中瞬间就可见（Invalidate 可能要等下一帧消息）。
        Refresh();
    }

    /// <summary>每 80ms 触发：清除过期标记，其余持续重绘以保持可见（约 1s）。</summary>
    private void OcrTimerTick()
    {
        bool any;
        lock (_ocrMarkers)
        {
            DateTime now = DateTime.UtcNow;
            _ocrMarkers.RemoveAll(m => now >= m.Expire);
            any = _ocrMarkers.Count > 0;
        }

        if (!any)
        {
            _ocrTimer.Enabled = false;
        }

        Invalidate();
    }

    /// <summary>把归一化命中矩形绘制为半透明红框 + 十字 + OCR 标签，叠加在设备画面上层。</summary>
    private void DrawOcrMarker(Graphics g, Rectangle r)
    {
        if (_videoW <= 0 || _videoH <= 0)
        {
            return;
        }

        List<(RectangleF Rect, DateTime Expire)> snapshot;
        lock (_ocrMarkers)
        {
            snapshot = _ocrMarkers.ToList();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        // ⭐ 关键修复：父路径在 1:1 拷贝时把合成模式设成了 SourceCopy，
        //    半透明红填充在 SourceCopy 下会绕过画面直接写回缓冲而几乎不可见。
        //    这里强制切回 SourceOver，让红框/标签正确叠加在视频画面上层。
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.GammaCorrected;

        foreach ((RectangleF nm, DateTime _) in snapshot)
        {
            float x = r.X + nm.X * r.Width;
            float y = r.Y + nm.Y * r.Height;
            float w = nm.Width * r.Width;
            float h = nm.Height * r.Height;
            if (w < 1 || h < 1)
            {
                continue;
            }

            var rect = new RectangleF(x, y, w, h);

            using (var fill = new SolidBrush(Color.FromArgb(120, 255, 48, 32)))
            {
                g.FillRectangle(fill, rect);
            }

            using var pen = new Pen(Color.FromArgb(255, 255, 224, 48), 3);
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            float cx = rect.X + rect.Width / 2;
            float cy = rect.Y + rect.Height / 2;
            g.DrawLine(pen, rect.X, cy, rect.Right, cy);
            g.DrawLine(pen, cx, rect.Y, cx, rect.Bottom);

            using var labelFont = new Font(UiTheme.PlaceholderFont.FontFamily, 11f, FontStyle.Bold);
            using var labelBrush = new SolidBrush(Color.FromArgb(255, 255, 224, 48));
            float labelY = rect.Y - 16 >= 0 ? rect.Y - 16 : rect.Y + 2;
            g.DrawString("OCR", labelFont, labelBrush, rect.X, labelY);
        }
    }

    /// <summary>把控件坐标换算到视频坐标。</summary>
    private bool TryMap(int mouseX, int mouseY, out int videoX, out int videoY)
    {
        return CoordinateMapper.TryMapToVideo(mouseX, mouseY, Width, Height, _videoW, _videoH,
                                              out videoX, out videoY);
    }

    /// <summary>画深灰底 + 居中提示文字（支持 <c>\n</c> 多行）。</summary>
    private void DrawPlaceholder(Graphics g)
    {
        using var brush = new SolidBrush(UiTheme.ScreenBackground);
        g.FillRectangle(brush, ClientRectangle);

        using var textBrush = new SolidBrush(UiTheme.PlaceholderText);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        var box = new RectangleF(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 16));
        g.DrawString(_placeholder, UiTheme.PlaceholderFont, textBrush, box, format);
    }

    /// <summary>通知宿主当前 letterbox 尺寸。</summary>
    private void RaiseLetterboxChanged()
    {
        Rectangle r = CurrentLetterbox();
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        LetterboxChanged?.Invoke(r.Width, r.Height);
    }
}
