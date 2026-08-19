using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using OpenCvSharp;

namespace MultiScrcpy.Core.Scripting;

/// <summary>
/// 基于 OpenCvSharp4 的模板匹配器，使用 alpha mask 传入 MatchTemplate 的匹配机制，
/// 完全复刻 D:\新建文件夹\OcrViewer 的 Vision.Match。
/// <para>
/// 整体流程对齐 OcrViewer 的 <c>Vision.Match</c>：
/// 1) <b>保留 alpha 读图</b>：PNG 内存流 + <c>Cv2.ImDecode(ImreadModes.Unchanged)</c>，与 OcrViewer 一致；
/// 2) <b>灰度匹配</b>：场景与模板都转单通道灰度，排除颜色光照干扰，只比形状/亮度；
/// 3) <b>透明 PNG 的 alpha 处理（关键）</b>：BGRA 模板 Split 出 BGR 与 alpha，
///    Merge BGR 为 <c>bgrMat</c>，<c>templGray = ToGray(bgrMat)</c>，
///    并由 <c>alpha</c> 经 <c>Threshold(alpha, 50, 255, Binary)</c> 得到二值 <c>mask</c>，
///    直接传入 <c>MatchTemplate</c>——仅非透明像素参与匹配，忽略镂空/半透明/抗锯齿背景，
///    与 OcrViewer 行为完全一致（金标准参考图对达 0.99+）。不再使用裁剪 bounding box 的方案；
/// 4) <b>多尺度搜索</b>：0.85–1.15 按 0.05 步进，模板缩放用 Linear，mask 缩放用 Nearest；
/// 5) <b>CCoeffNormed</b> + <c>MinMaxLoc</c> 取 maxVal / maxLoc；并对 maxVal 做有限值防御。
/// </para>
/// </summary>
internal sealed class OpenCvTemplateMatcher : ITemplateMatcher
{
    // 多尺度搜索范围：0.85–1.15（步进 0.05，共 7 个尺度）。
    // 视频流与手机原图分辨率接近 1:1，此窄带已足够覆盖不同机型分辨率差异；
    // 去掉 0.6–0.85 与 1.15–1.4 极端尺度以避免非 1.0 尺度上的伪命中（曾导致 Nx 偏离 0.466）。
    private const double ScaleMin = 0.85;
    private const double ScaleMax = 1.1501;
    private const double ScaleStep = 0.05;

    // 透明像素判定阈值（与 OcrViewer Vision.Match 的 50 一致）。
    private const double AlphaThreshold = 50.0;

    /// <summary>库是否可用（静态构造时探测一次；原生运行时缺失则为 false，引擎回退到托管实现）。</summary>
    public static bool IsAvailable { get; }

    static OpenCvTemplateMatcher()
    {
        bool ok = false;
        try
        {
            // 用一次真实原生 Mat 操作探测 OpenCvSharp4 运行时是否就位（缺失会抛 DllNotFoundException）。
            using var a = new Mat(2, 2, MatType.CV_8UC1);
            using var b = a.Clone();
            using var res = a.MatchTemplate(b, TemplateMatchModes.CCoeffNormed);
            ok = res.Rows > 0 && res.Cols > 0;
        }
        catch
        {
            ok = false;
        }

        IsAvailable = ok;
    }

    // NMS 去重阈值：从 0.5 收紧到 0.3，避免轻微偏移/不同尺度的重叠候选都被保留导致
    // 最优位置被错误候选挤掉（曾出现 score=1.00 命中 Nx=0.601 的错误位置）。
    private const double IouThreshold = 0.3;

    /// <inheritdoc />
    public TemplateMatch? Match(Bitmap frame, Bitmap template, double maxError)
    {
        var all = MatchAll(frame, template, maxError, maxResults: 1);
        return all.Count > 0 ? all[0] : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<TemplateMatch> MatchAll(Bitmap frame, Bitmap template, double maxError, int maxResults = 10)
    {
        if (!IsAvailable || frame == null || template == null)
        {
            return Array.Empty<TemplateMatch>();
        }

        int fw = frame.Width;
        int fh = frame.Height;
        if (fw <= 0 || fh <= 0 || template.Width <= 0 || template.Height <= 0)
        {
            return Array.Empty<TemplateMatch>();
        }

        // 帧 -> Mat（保留通道，随后转灰度）。走 PNG 流 + ImDecode，与 OcrViewer 读图方式一致。
        using Mat? scene = BitmapToMat(frame);
        if (scene == null || scene.Empty())
        {
            return Array.Empty<TemplateMatch>();
        }

        // 模板 -> Mat（保留 alpha；透明区域交由 mask 处理，不参与裁剪）。
        using Mat? templ = BitmapToMat(template);
        if (templ == null || templ.Empty())
        {
            return Array.Empty<TemplateMatch>();
        }

        using Mat sceneGray = ToGray(scene);

        // 由模板生成灰度图与可选 alpha 遮罩（完全复刻 OcrViewer Vision.Match）。
        // templGray 通过 using 释放；mask 在循环结束后手动 Dispose（若非 null）。
        using Mat templGray = BuildTemplGrayAndMask(templ, out Mat? mask);
        if (templGray.Empty())
        {
            // 无可匹配内容。
            mask?.Dispose();
            return Array.Empty<TemplateMatch>();
        }

        double threshold = Math.Max(0.0, 1.0 - maxError);
        var candidates = new List<RawCandidate>();

        for (double s = ScaleMin; s <= ScaleMax; s += ScaleStep)
        {
            using Mat rTempl = templGray.Resize(new OpenCvSharp.Size(0, 0), s, s, InterpolationFlags.Linear);
            if (rTempl.Width > sceneGray.Width || rTempl.Height > sceneGray.Height)
            {
                continue;
            }

            // mask 缩放：仅在非 1.0 尺度且存在 mask 时按 Nearest 重采样，保持二值语义。
            Mat? rMask = mask;
            bool resizedMask = false;
            if (mask != null && Math.Abs(s - 1.0) > 1e-6)
            {
                rMask = mask.Resize(new OpenCvSharp.Size(0, 0), s, s, InterpolationFlags.Nearest);
                resizedMask = true;
            }

            using Mat res = sceneGray.MatchTemplate(rTempl, TemplateMatchModes.CCoeffNormed, rMask!);

            // MatchTemplate 输出类型通常为 CV_32F，而后续按 CV_64F 遍历读取。
            // 原地转换为 double 矩阵，避免 res.At<double> 读到错误的位模式（会导致 score
            // 变成巨大垃圾值，进而使 NMS 排序与 anchor/target 选择全部错乱）。
            if (res.Type() != MatType.CV_64FC1)
            {
                res.ConvertTo(res, MatType.CV_64FC1);
            }

            // 收集本尺度所有合格的局部极大值 + 全局最佳值（防止 plateau 漏检）。
            CollectCandidates(res, rTempl.Width, rTempl.Height, threshold, candidates);

            // rMask 若是本尺度新生成的缩放遮罩则释放；原始 mask 引用不在此释放。
            if (resizedMask)
            {
                rMask!.Dispose();
            }
        }

        // 释放 alpha 遮罩（若有）。
        mask?.Dispose();

        if (candidates.Count == 0)
        {
            return Array.Empty<TemplateMatch>();
        }

        // NMS 去重，按 score 降序。
        List<RawCandidate> kept = Nms(candidates, IouThreshold);
        kept.Sort((a, b) => b.Score.CompareTo(a.Score));

        int count = Math.Min(maxResults, kept.Count);
        var results = new List<TemplateMatch>(count);
        for (int i = 0; i < count; i++)
        {
            RawCandidate c = kept[i];

            // 防御性位置校验：候选框必须完整落在帧内。正常情况下 MatchTemplate 输出坐标
            // 自带该约束（X ∈ [0, fw-tplW], Y ∈ [0, fh-tplH]），但若上游误用（如手工构造
            // 候选）需拦截，避免后续归一化坐标/点击越界。
            if (c.X < 0 || c.Y < 0 || c.X + c.W > fw || c.Y + c.H > fh)
            {
                continue;
            }

            double nx = (c.X + c.W / 2.0) / fw;
            double ny = (c.Y + c.H / 2.0) / fh;
            double halfW = (c.W / 2.0) / fw;
            double halfH = (c.H / 2.0) / fh;
            results.Add(new TemplateMatch(nx, ny, halfW, halfH, c.Score));
        }

        // 诊断：仅在最高分不"近 1"时记录候选数，便于排查"score=1.00 但位置错"的回归。
        // 走 Debug 通道，避免污染普通日志（普通路径只在异常时打）。
        if (results.Count > 0 && results[0].Score < 0.999)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TemplateMatcher] {candidates.Count} 个候选，NMS 保留 {kept.Count} 个（IoU={IouThreshold:F2}）；最佳 score={results[0].Score:F4} loc=({results[0].Nx:F3},{results[0].Ny:F3})");
        }

        return results;
    }

    #region Helpers

    /// <summary>原始候选（像素坐标）。</summary>
    private readonly record struct RawCandidate(int X, int Y, int W, int H, double Score);

    /// <summary>从单尺度 MatchTemplate 结果中收集合格的局部极大值与全局最佳值。</summary>
    private static void CollectCandidates(Mat res, int tw, int th, double threshold, List<RawCandidate> candidates)
    {
        Cv2.MinMaxLoc(res, out _, out double globalMax, out _, out OpenCvSharp.Point globalLoc);

        // 有限值防御：纯色退化窗口可能产出 Inf/NaN。
        if (!double.IsFinite(globalMax))
        {
            (globalMax, globalLoc) = FindBestFinite(res);
            if (!double.IsFinite(globalMax))
            {
                return;
            }
        }

        if (globalMax < threshold)
        {
            return;
        }

        // 全局最佳值总是加入候选：防止结果图 plateau 或峰值不够尖锐时漏检。
        candidates.Add(new RawCandidate(globalLoc.X, globalLoc.Y, tw, th, globalMax));

        int rows = res.Rows;
        int cols = res.Cols;
        for (int y = 1; y < rows - 1; y++)
        {
            for (int x = 1; x < cols - 1; x++)
            {
                if (x == globalLoc.X && y == globalLoc.Y)
                {
                    continue;
                }

                double v = res.At<double>(y, x);
                if (!double.IsFinite(v) || v < threshold)
                {
                    continue;
                }

                bool isMax = true;
                for (int dy = -1; dy <= 1 && isMax; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        if (res.At<double>(y + dy, x + dx) >= v)
                        {
                            isMax = false;
                            break;
                        }
                    }
                }

                if (isMax)
                {
                    candidates.Add(new RawCandidate(x, y, tw, th, v));
                }
            }
        }
    }

    /// <summary>非极大值抑制，按 score 降序保留候选。</summary>
    private static List<RawCandidate> Nms(List<RawCandidate> candidates, double threshold)
    {
        var sorted = new List<RawCandidate>(candidates);
        sorted.Sort((a, b) => b.Score.CompareTo(a.Score));

        var keep = new List<RawCandidate>(sorted.Count);
        foreach (RawCandidate c in sorted)
        {
            bool suppressed = false;
            foreach (RawCandidate k in keep)
            {
                if (ComputeIoU(c, k) > threshold)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                keep.Add(c);
            }
        }

        return keep;
    }

    /// <summary>计算两个候选框的 IoU（像素坐标）。</summary>
    private static double ComputeIoU(RawCandidate a, RawCandidate b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.W, b.X + b.W);
        int y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        int interW = Math.Max(0, x2 - x1);
        int interH = Math.Max(0, y2 - y1);
        double inter = (double)interW * interH;
        double area = (double)a.W * a.H + (double)b.W * b.H - inter;
        return area > 0.0 ? inter / area : 0.0;
    }

    /// <summary>
    /// 把 Bitmap 转为 Mat：编码为 PNG 内存流后用 <c>Cv2.ImDecode(ImreadModes.Unchanged)</c> 解码，
    /// 与 OcrViewer 的 <c>Vision.Load</c> 完全一致，并保留 4 通道 alpha（供透明模板 mask 生成）。
    /// </summary>
    private static Mat? BitmapToMat(Bitmap bitmap)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            byte[] bytes = ms.ToArray();
            Mat decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
            return decoded != null && !decoded.Empty() ? decoded : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从模板 Mat 生成灰度图与可选 alpha 遮罩（完全复刻 OcrViewer Vision.Match）。
    /// <para>
    /// BGRA 模板：<c>Split</c> 出 BGR 与 alpha；<c>Merge</c> BGR 为 <c>bgrMat</c>，
    /// <c>templGray = ToGray(bgrMat)</c>；<c>mask = Threshold(alpha, 50, 255, Binary)</c>。
    /// 3 通道模板：无 mask（<paramref name="mask"/> 返回 null）。
    /// 返回的灰度 Mat 由调用方（<c>using</c>）负责释放；<paramref name="mask"/> 在循环结束后手动释放。
    /// <c>Split</c> 产生的每个 <c>Mat</c> 均在本方法内释放；<c>bgrMat</c> 在生成灰度后释放；
    /// <c>alpha</c> 在生成 mask 后释放。
    /// </para>
    /// </summary>
    private static Mat BuildTemplGrayAndMask(Mat templ, out Mat? mask)
    {
        Mat bgr;
        Mat? alpha = null;
        if (templ.Channels() == 4)
        {
            Mat[] planes = templ.Split();
            bgr = new Mat();
            Cv2.Merge(new Mat[] { planes[0], planes[1], planes[2] }, bgr);
            alpha = planes[3];
            planes[0].Dispose();
            planes[1].Dispose();
            planes[2].Dispose();
        }
        else
        {
            bgr = templ.Clone();
        }

        Mat templGray = ToGray(bgr);
        bgr.Dispose();

        if (alpha != null)
        {
            mask = new Mat();
            Cv2.Threshold(alpha, mask, AlphaThreshold, 255, ThresholdTypes.Binary);
            alpha.Dispose();
        }
        else
        {
            mask = null;
        }

        return templGray;
    }

    /// <summary>转单通道灰度（对齐 OcrViewer ToGray：1 通道原样、4 通道 BGRA2GRAY、3 通道 BGR2GRAY）。</summary>
    private static Mat ToGray(Mat src)
    {
        if (src.Channels() == 1)
        {
            return src.Clone();
        }

        if (src.Channels() == 4)
        {
            return src.CvtColor(ColorConversionCodes.BGRA2GRAY);
        }

        return src.CvtColor(ColorConversionCodes.BGR2GRAY);
    }

    /// <summary>
    /// 在 <c>MatchTemplate</c> 结果中扫描最佳有限值。
    /// <para>
    /// 当场景中存在纯色退化窗口时， masked <c>CCoeffNormed</c> 会在局部产出 Inf/NaN；
    /// <c>MinMaxLoc</c> 返回 Inf 后，需要遍历矩阵找到最大的有限值及其位置。
    /// </para>
    /// </summary>
    private static (double Value, OpenCvSharp.Point Location) FindBestFinite(Mat res)
    {
        int rows = res.Rows;
        int cols = res.Cols;
        double best = double.NegativeInfinity;
        int bx = 0, by = 0;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                double v = res.At<double>(y, x);
                if (double.IsFinite(v) && v > best)
                {
                    best = v;
                    bx = x;
                    by = y;
                }
            }
        }

        return (best, new OpenCvSharp.Point(bx, by));
    }

    #endregion
}
