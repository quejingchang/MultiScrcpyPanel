using System.Drawing;

namespace MultiScrcpy.Core.Scripting;

/// <summary>单次模板匹配结果（归一化坐标系，0–1 相对视频帧）。</summary>
public sealed class TemplateMatch
{
    public TemplateMatch(double nx, double ny, double halfW, double halfH, double score)
    {
        Nx = nx;
        Ny = ny;
        HalfW = halfW;
        HalfH = halfH;
        Score = score;
    }

    /// <summary>命中中心 x（归一化 0–1）。</summary>
    public double Nx { get; }

    /// <summary>命中中心 y（归一化 0–1）。</summary>
    public double Ny { get; }

    /// <summary>命中框半宽（归一化）。完整框 = [Nx±HalfW, Ny±HalfH]。</summary>
    public double HalfW { get; }

    /// <summary>命中框半高（归一化）。</summary>
    public double HalfH { get; }

    /// <summary>相似度 0–1（越高越像）。</summary>
    public double Score { get; }
}

/// <summary>
/// 模板匹配器抽象：在屏幕帧中定位一张小图（图标 / 文字块）。
/// <para>实现：<see cref="OpenCvTemplateMatcher"/>（OpenCvSharp4，照搬 OcrViewer 的 Vision.Match）。
/// 2026-08-19 起移除旧纯托管 <c>ManagedTemplateMatcher</c> 回退路线，由 <see cref="TemplateMatcherFactory"/> 统一提供。</para>
/// </summary>
public interface ITemplateMatcher
{
    /// <summary>在 frame 中查找 template；命中返回 <see cref="TemplateMatch"/>，否则 null。</summary>
    /// <param name="maxError">允许的最大不相似度（0–1）。相似度需 ≥ 1 - maxError 才算命中。</param>
    TemplateMatch? Match(Bitmap frame, Bitmap template, double maxError);

    /// <summary>
    /// 在 frame 中查找 template 的<b>所有命中位置</b>。
    /// <para>
    /// 返回所有相似度 ≥ <c>1 - maxError</c> 的候选，按 score 降序排列，并经过 NMS 去重。
    /// </para>
    /// </summary>
    /// <param name="maxError">允许的最大不相似度（0–1）。</param>
    /// <param name="maxResults">最多返回的候选数（默认 10）。</param>
    IReadOnlyList<TemplateMatch> MatchAll(Bitmap frame, Bitmap template, double maxError, int maxResults = 10);
}
