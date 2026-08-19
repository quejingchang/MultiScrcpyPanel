using System.Collections.Generic;
using System.Linq;

using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 双通道融合去重逻辑（<see cref="TesseractTextRecognizer.MergeAndDedupeWords"/>）纯单元测试。
/// <para>不依赖 tesseract / 语言包 / 真实模板，只验证合并去重规则本身。</para>
/// </summary>
public class WordMergeTests
{
    [Fact]
    public void 相同文字且中心接近_只保留通道A版本()
    {
        var a = new RecognizedTextLine("使用", 0.30, 0.50, 0.10, 0.05);
        var b = new RecognizedTextLine("使用", 0.31, 0.50, 0.10, 0.05); // 中心距离 0.01 ≤ 0.02

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a }, new List<RecognizedTextLine> { b });

        Assert.Single(merged);
        // 保留通道 A 的版本（含其坐标），避免通道 B 的轻微坐标偏移。
        Assert.Equal(a.Text, merged[0].Text);
        Assert.Equal(a.X, merged[0].X);
        Assert.Equal(a.Y, merged[0].Y);
    }

    [Fact]
    public void 相同文字但中心距离远_两个都保留()
    {
        var a = new RecognizedTextLine("使用", 0.10, 0.10, 0.10, 0.05);
        var b = new RecognizedTextLine("使用", 0.60, 0.60, 0.10, 0.05); // 中心距离 ≈ 0.707 > 0.02

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a }, new List<RecognizedTextLine> { b });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void 文字不同_即使中心接近也保留()
    {
        var a = new RecognizedTextLine("参加", 0.30, 0.50, 0.10, 0.05);
        var b = new RecognizedTextLine("使用", 0.30, 0.50, 0.10, 0.05);

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a }, new List<RecognizedTextLine> { b });

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, w => w.Text == "参加");
        Assert.Contains(merged, w => w.Text == "使用");
    }

    [Fact]
    public void 通道A为空_直接返回通道B()
    {
        var b = new RecognizedTextLine("帮派任务", 0.20, 0.20, 0.10, 0.05);

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine>(), new List<RecognizedTextLine> { b });

        Assert.Single(merged);
        Assert.Equal("帮派任务", merged[0].Text);
        Assert.Equal(b.X, merged[0].X);
    }

    [Fact]
    public void 通道B为空_直接返回通道A()
    {
        var a = new RecognizedTextLine("师门任务", 0.20, 0.20, 0.10, 0.05);

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a }, new List<RecognizedTextLine>());

        Assert.Single(merged);
        Assert.Equal("师门任务", merged[0].Text);
    }

    [Fact]
    public void 混合场景_保留通道A结果并追加通道B独有词()
    {
        var aWords = new List<RecognizedTextLine>
        {
            new("师门任务", 0.20, 0.20, 0.12, 0.05),
            new("参加", 0.20, 0.30, 0.10, 0.05),
        };
        var bWords = new List<RecognizedTextLine>
        {
            new("师门任务", 0.205, 0.20, 0.12, 0.05), // 与 A 重复 → 丢弃
            new("参加", 0.20, 0.30, 0.10, 0.05),      // 与 A 重复 → 丢弃
            new("帮派任务", 0.60, 0.40, 0.12, 0.05),  // A 没有 → 追加
        };

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(aWords, bWords);

        Assert.Equal(3, merged.Count);
        Assert.Equal("师门任务", merged[0].Text);
        Assert.Equal(0.20, merged[0].X); // 保留通道 A 坐标
        Assert.Equal("参加", merged[1].Text);
        Assert.Equal("帮派任务", merged[2].Text);
    }

    [Fact]
    public void 两通道皆空_返回空()
    {
        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine>(), new List<RecognizedTextLine>());

        Assert.Empty(merged);
    }

    [Fact]
    public void 去重中心距离阈值_为0_02()
    {
        Assert.Equal(0.02, TesseractTextRecognizer.DedupeCenterDistanceThreshold);
    }

    // ---- 三通道融合（通道 A 灰度 + 通道 B Otsu + 通道 C HSV-V）合并去重 ----

    [Fact]
    public void 三路都命中同一词_保留通道A版本()
    {
        // 三路词中心两两距离都 ≤ 阈值（0.02），按 A > B > C 优先级保留 A 的坐标。
        var a = new RecognizedTextLine("参加", 0.30, 0.50, 0.10, 0.05); // centerX=0.35, centerY=0.525
        var b = new RecognizedTextLine("参加", 0.31, 0.50, 0.10, 0.05); // centerX=0.36, 距 A 0.01
        var c = new RecognizedTextLine("参加", 0.315, 0.50, 0.10, 0.05); // centerX=0.365, 距 A 0.015 / 距 B 0.005

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a },
            new List<RecognizedTextLine> { b },
            new List<RecognizedTextLine> { c });

        Assert.Single(merged);
        Assert.Equal(a.Text, merged[0].Text);
        Assert.Equal(a.X, merged[0].X);
        Assert.Equal(a.Y, merged[0].Y);
    }

    [Fact]
    public void 三路合并_A空_B和C各有不同词_保留两个()
    {
        // A 完全失败（灰度+2x 救不回），B 救回 "帮派任务"，C 救回 "参加"（V 通道救回），
        // 两者中心距离远超阈值，互不重复。
        var b = new RecognizedTextLine("帮派任务", 0.20, 0.20, 0.12, 0.05);
        var c = new RecognizedTextLine("参加", 0.70, 0.70, 0.10, 0.05);

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine>(),
            new List<RecognizedTextLine> { b },
            new List<RecognizedTextLine> { c });

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, w => w.Text == "帮派任务");
        Assert.Contains(merged, w => w.Text == "参加");
    }

    [Fact]
    public void 三路合并_A与C同词但距离远_两个都保留()
    {
        // A 命中 "参加" 在左上角，C 命中 "参加" 在右下角——距离远超阈值，
        // 不能视为同一词（如两个相同按钮分处不同控件区域），两个都保留。
        var a = new RecognizedTextLine("参加", 0.10, 0.10, 0.10, 0.05);
        var c = new RecognizedTextLine("参加", 0.60, 0.60, 0.10, 0.05);

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine> { a },
            new List<RecognizedTextLine>(),
            new List<RecognizedTextLine> { c });

        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged.Count(w => w.Text == "参加"));
    }

    [Fact]
    public void 三路合并_A空_B与C同词_保留通道B版本()
    {
        // A 完全失败，B 和 C 都识别出 "参加" 但中心接近（≤ 阈值），
        // 按 B > C 优先级保留 B 的坐标（V 通道不应覆盖 Otsu 救回结果）。
        var b = new RecognizedTextLine("参加", 0.30, 0.50, 0.10, 0.05);
        var c = new RecognizedTextLine("参加", 0.31, 0.50, 0.10, 0.05); // 距 B 0.01 ≤ 0.02

        var merged = TesseractTextRecognizer.MergeAndDedupeWords(
            new List<RecognizedTextLine>(),
            new List<RecognizedTextLine> { b },
            new List<RecognizedTextLine> { c });

        Assert.Single(merged);
        Assert.Equal(b.Text, merged[0].Text);
        Assert.Equal(b.X, merged[0].X);
        Assert.Equal(b.Y, merged[0].Y);
    }
}
