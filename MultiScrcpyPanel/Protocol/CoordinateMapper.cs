using System;
using System.Drawing;

namespace MultiScrcpy.Protocol;

/// <summary>
/// letterbox（等比居中 + 黑边）坐标换算，逐行对应 Python 版 <c>_map_to_video</c>（架构文档 §5.5）。
/// <para>
/// 全部为 <b>纯静态方法</b>，不依赖任何 WinForms 控件，可在无头环境下单测。
/// 返回类型 <see cref="Rectangle"/> 来自 <c>System.Drawing.Primitives</c>（BCL，非 WinForms）。
/// </para>
/// </summary>
public static class CoordinateMapper
{
    /// <summary>
    /// 把控件坐标映射到视频帧坐标。
    /// </summary>
    /// <param name="mouseX">控件坐标系鼠标 x。</param>
    /// <param name="mouseY">控件坐标系鼠标 y。</param>
    /// <param name="ctrlW">控件宽。</param>
    /// <param name="ctrlH">控件高。</param>
    /// <param name="videoW">视频帧宽。</param>
    /// <param name="videoH">视频帧高。</param>
    /// <param name="vx">输出：视频帧坐标 x，已 clamp 到 [0, videoW-1]。</param>
    /// <param name="vy">输出：视频帧坐标 y，已 clamp 到 [0, videoH-1]。</param>
    /// <returns>任一尺寸非正时返回 false（此时输出为 -1）。</returns>
    public static bool TryMapToVideo(int mouseX, int mouseY,
                                     int ctrlW, int ctrlH,
                                     int videoW, int videoH,
                                     out int vx, out int vy)
    {
        vx = -1;
        vy = -1;
        if (videoW <= 0 || videoH <= 0 || ctrlW <= 0 || ctrlH <= 0) return false;

        double scale = Math.Min((double)ctrlW / videoW, (double)ctrlH / videoH);
        if (scale <= 0) return false;

        double dispW = videoW * scale;
        double dispH = videoH * scale;
        double offX = (ctrlW - dispW) / 2.0;
        double offY = (ctrlH - dispH) / 2.0;

        // 向零截断，与 Python int() 行为一致
        int x = (int)((mouseX - offX) / scale);
        int y = (int)((mouseY - offY) / scale);

        vx = Math.Max(0, Math.Min(videoW - 1, x));
        vy = Math.Max(0, Math.Min(videoH - 1, y));
        return true;
    }

    /// <summary>
    /// OnPaint 用：视频等比居中后在控件内的目标矩形。
    /// </summary>
    public static Rectangle ComputeLetterbox(int ctrlW, int ctrlH, int videoW, int videoH)
    {
        if (videoW <= 0 || videoH <= 0 || ctrlW <= 0 || ctrlH <= 0) return Rectangle.Empty;

        double scale = Math.Min((double)ctrlW / videoW, (double)ctrlH / videoH);
        int w = (int)Math.Round(videoW * scale);
        int h = (int)Math.Round(videoH * scale);
        if (w <= 0) w = 1;
        if (h <= 0) h = 1;

        return new Rectangle((ctrlW - w) / 2, (ctrlH - h) / 2, w, h);
    }
}
