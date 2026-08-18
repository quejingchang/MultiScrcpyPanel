using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace MultiScrcpy.Core;

/// <summary>
/// 三缓冲位图交换（架构文档 §1.6）。
/// <para>
/// <c>worker(_back) --Publish()--&gt; _ready --Acquire()--&gt; UI(_front)</c>
/// </para>
/// <para>
/// 目标：<b>latest-frame-wins</b>，位图复用不反复 new，消除每帧 GC 压力。
/// 同一时刻同一块 <see cref="Bitmap"/> 只被一个线程访问：worker 只碰 <c>_back</c>，UI 只碰 <c>_front</c>。
/// </para>
/// <para>
/// ⚠️ <see cref="Resize"/> 只在 worker 线程调用，且 <b>不释放 <c>_front</c></b>
/// （UI 可能正在 DrawImage）——旧的 <c>_front</c> 由 UI 线程在下一次 <see cref="Acquire"/> 时按尺寸不符自行释放，
/// 从根源避免 GDI+「参数无效」崩溃。
/// </para>
/// </summary>
public sealed class FrameBuffer : IDisposable
{
    private const PixelFormat Format = PixelFormat.Format24bppRgb;

    private readonly object _lock = new();

    private Bitmap? _back;
    private Bitmap? _ready;
    private Bitmap? _front;
    private int _w;
    private int _h;
    private bool _hasNew;
    private bool _disposed;
    private long _seq;

    /// <summary>当前渲染目标宽度（未初始化为 0）。</summary>
    public int Width
    {
        get { lock (_lock) return _w; }
    }

    /// <summary>当前渲染目标高度（未初始化为 0）。</summary>
    public int Height
    {
        get { lock (_lock) return _h; }
    }

    /// <summary>已发布的帧序号，仅用于诊断。</summary>
    public long Sequence => Interlocked.Read(ref _seq);

    /// <summary>
    /// 重建缓冲（仅 worker 线程调用）：尺寸不变则直接返回。
    /// </summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        lock (_lock)
        {
            if (_disposed) return;
            if (_w == width && _h == height && _back != null && _ready != null) return;

            _w = width;
            _h = height;

            try { _back?.Dispose(); } catch { /* 尽力清理 */ }
            try { _ready?.Dispose(); } catch { /* 尽力清理 */ }

            _back = new Bitmap(width, height, Format);
            _ready = new Bitmap(width, height, Format);
            _hasNew = false;
            // _front 故意不释放：UI 线程可能正在绘制它
        }
    }

    /// <summary>取得可写入的后台位图（worker 线程）。未初始化时返回 null。</summary>
    public Bitmap? BeginRender()
    {
        lock (_lock)
        {
            return _disposed ? null : _back;
        }
    }

    /// <summary>发布刚渲染完的帧：交换 <c>_back ⇄ _ready</c>（worker 线程）。</summary>
    public void Publish()
    {
        lock (_lock)
        {
            if (_disposed || _back == null || _ready == null) return;

            (_back, _ready) = (_ready, _back);
            _hasNew = true;
            Interlocked.Increment(ref _seq);
        }
    }

    /// <summary>
    /// 取得最新一帧用于绘制（UI 线程）：有新帧则交换 <c>_front ⇄ _ready</c>，否则返回上一帧。
    /// 尚未 <see cref="Resize"/> 时返回 null，调用方需处理。
    /// </summary>
    public Bitmap? Acquire()
    {
        lock (_lock)
        {
            if (_disposed) return null;

            // 分辨率变化后，旧的 _front 尺寸不符 → 此刻由 UI 线程安全释放
            if (_front != null && (_front.Width != _w || _front.Height != _h))
            {
                try { _front.Dispose(); } catch { /* 尽力清理 */ }
                _front = null;
            }

            if (_hasNew && _ready != null)
            {
                if (_front == null)
                {
                    _front = _ready;
                    _ready = new Bitmap(_w, _h, Format);
                }
                else
                {
                    (_front, _ready) = (_ready, _front);
                }

                _hasNew = false;
            }

            return _front;
        }
    }

    /// <summary>
    /// 取得当前帧的只读快照（深拷贝），供脚本做模板匹配等图像分析。
    /// <para>与 <see cref="Acquire"/> 不同：本方法不消费新帧、不交换缓冲，UI 渲染线程不受影响。</para>
    /// </summary>
    public Bitmap? GetSnapshot()
    {
        lock (_lock)
        {
            if (_disposed || _front == null)
            {
                return null;
            }

            try
            {
                return _front.Clone(new Rectangle(0, 0, _front.Width, _front.Height), _front.PixelFormat);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try { _back?.Dispose(); } catch { /* 尽力清理 */ }
            try { _ready?.Dispose(); } catch { /* 尽力清理 */ }
            try { _front?.Dispose(); } catch { /* 尽力清理 */ }

            _back = null;
            _ready = null;
            _front = null;
            _w = 0;
            _h = 0;
            _hasNew = false;
        }
    }
}
