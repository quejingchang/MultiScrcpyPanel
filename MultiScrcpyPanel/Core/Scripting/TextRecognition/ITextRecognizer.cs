using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace MultiScrcpy.Core.Scripting.TextRecognition;

/// <summary>
/// 真实文字识别（OCR）抽象：把帧/截图转换为带归一化位置的文字行/词。
/// <para>
/// 与现有模板匹配式的 "OCR" 指令不同，本接口识别<b>文字内容</b>，
/// 用于 "OCR 文字点击" 步骤：先找到指定文本，再点击其相对偏移位置。
/// </para>
/// </summary>
public interface ITextRecognizer : IDisposable
{
    /// <summary>当前识别器是否可用（语言包/原生依赖已就位）。</summary>
    bool IsAvailable { get; }

    /// <summary>识别图片中的全部文字行/词并返回其归一化位置（x/y/w/h ∈ 0–1）。</summary>
    Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(Bitmap bitmap, CancellationToken token = default);

    /// <summary>
    /// 识别图片中的<b>词</b>并返回每个词的真实归一化包围盒（用于"在模板内定位文字"场景）。
    /// <para>与 <see cref="RecognizeAsync"/> 不同：本方法返回带坐标的词框，而非整行占位框。</para>
    /// </summary>
    Task<IReadOnlyList<RecognizedTextLine>> RecognizeWordsAsync(Bitmap bitmap, CancellationToken token = default);
}

/// <summary>一行（或一词）识别结果，坐标已归一化到图片宽高。</summary>
public sealed record RecognizedTextLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2.0;

    public double CenterY => Y + Height / 2.0;

    public double Right => X + Width;

    public double Bottom => Y + Height;
}
