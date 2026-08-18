using System.Collections.Generic;

using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;

namespace MultiScrcpy.Tests;

public class TextMatcherTests
{
    [Fact]
    public void 精确匹配_返回命中()
    {
        var hit = TextMatcher.FindBest("宝图任务", new[]
        {
            new RecognizedTextLine("师门任务", 0, 0, 0.1, 0.05),
            new RecognizedTextLine("宝图任务", 0.2, 0.2, 0.1, 0.05)
        }, 0.2);

        Assert.NotNull(hit);
        Assert.Equal("宝图任务", hit!.Text);
    }

    [Fact]
    public void 候选包含目标_按包含命中()
    {
        // OCR 把相邻文字连成一个候选（如游戏 UI 中"师门任务参加"被识别为一个词）
        var hit = TextMatcher.FindBest("师门任务", new[]
        {
            new RecognizedTextLine("师门任务参加", 0.1, 0.1, 0.3, 0.05)
        }, 0.2);

        Assert.NotNull(hit);
        Assert.Equal("师门任务参加", hit!.Text);
    }

    [Fact]
    public void 编辑距离命中_容错内()
    {
        var hit = TextMatcher.FindBest("宝图任务", new[]
        {
            new RecognizedTextLine("宝图任条", 0.2, 0.2, 0.1, 0.05)
        }, 0.3);

        Assert.NotNull(hit);
        Assert.Equal("宝图任条", hit!.Text);
    }

    [Fact]
    public void 无匹配_返回null()
    {
        var hit = TextMatcher.FindBest("宝图任务", new[]
        {
            new RecognizedTextLine("完全无关", 0, 0, 0.1, 0.05)
        }, 0.2);

        Assert.Null(hit);
    }

    [Fact]
    public void TopCandidates_按误差排序()
    {
        IReadOnlyList<TextMatcher.Candidate> top = TextMatcher.TopCandidates("宝图任务", new[]
        {
            new RecognizedTextLine("师门任务", 0, 0, 0.1, 0.05),
            new RecognizedTextLine("宝图任务参加", 0.1, 0.1, 0.3, 0.05),
            new RecognizedTextLine("宝图任条", 0.2, 0.2, 0.1, 0.05)
        }, 3);

        Assert.Equal(3, top.Count);
        Assert.Equal("宝图任务参加", top[0].Text); // 包含目标，误差 0
        Assert.Equal("宝图任条", top[1].Text);     // 编辑距离 0.25
        Assert.Equal("师门任务", top[2].Text);     // 编辑距离 0.5
    }
}
