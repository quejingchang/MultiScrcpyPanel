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

    // ---- FindBestSpan：单字命中优先于多字合并（日常运镖点击错位 BugFix） ----

    [Fact]
    public void FindBestSpan_单字命中优先于多字合并_日常运镖场景()
    {
        // 核心 Bug 复现：候选含"运""镖""电"等与目标无关的高置信词，且阅读顺序靠前。
        // 旧逻辑从"运"开始顺次合并 '运'+'镖'+'电'+'参加' → 返回"运镖电参加"并集中心
        // （被前缀拉向模板中部）；修复后应优先返回单词"参加"本身（无前缀干扰时）。
        // 候选列表（按阅读顺序 y/x 升序，但 FindBestSpan 内部会排序）
        var words = new List<RecognizedTextLine>
        {
            new("gm",   0.13, 0.18, 0.05, 0.03),  // 噪词
            new("运",   0.31, 0.28, 0.05, 0.06),  // 候选起点之一
            new("镖",   0.35, 0.31, 0.05, 0.06),  // 候选起点之一
            new("电",   0.10, 0.44, 0.05, 0.06),  // 候选起点之一
            new("参加", 0.83, 0.51, 0.10, 0.10),  // 真正的目标字
        };
        var (box, err) = TextMatcher.FindBestSpan("参加", words);
        Assert.NotNull(box);
        Assert.Equal(0, err, 5);
        // 修复后应优先返回'参加'单词（0.83, 0.51 附近），不是"运镖电参加"并集
        Assert.True(box.CenterX > 0.7, $"应返回'参加'单词位置(0.83)，实际中心 x={box.CenterX:F3}");
        Assert.True(box.CenterY > 0.4 && box.CenterY < 0.6, $"应返回'参加'单词 y≈0.51，实际 y={box.CenterY:F3}");
        // 返回的就是单词'参加'本身，而非并集文本
        Assert.Equal("参加", box.Text);
    }

    [Fact]
    public void FindBestSpan_长前缀单词包含目标_短词优先()
    {
        // 极端场景：Tesseract 把"运镖电参加"合并成单个词，同时又识别出"参加"。
        // 两者都包含目标（err=0），必须选文本更短的"参加"（其包围盒才贴近真实按钮）。
        var words = new List<RecognizedTextLine>
        {
            new("运镖电参加", 0.10, 0.28, 0.80, 0.10),
            new("参加", 0.83, 0.51, 0.10, 0.10),
        };
        var (box, err) = TextMatcher.FindBestSpan("参加", words);
        Assert.NotNull(box);
        Assert.Equal(0, err, 5);
        Assert.Equal("参加", box!.Text);
        Assert.True(box.CenterX > 0.7, $"应选短词'参加'(0.83)，实际中心 x={box.CenterX:F3}");
    }

    [Fact]
    public void FindBestSpan_无单字命中_回退多字合并_参加拆词()
    {
        // 兼容性：Tesseract 把"参加"拆成"参""加"两个词时，单字扫描无 err=0 命中，
        // 必须回退到"顺次扩展合并"，拼出"参加"并返回并集包围盒。
        var words = new List<RecognizedTextLine>
        {
            new("参", 0.20, 0.30, 0.10, 0.10),
            new("加", 0.35, 0.30, 0.10, 0.10),
        };
        var (box, err) = TextMatcher.FindBestSpan("参加", words);
        Assert.NotNull(box);
        Assert.Equal(0, err, 5);
        Assert.True(box.CenterX > 0.2 && box.CenterX < 0.4, $"合并中心应在'参''加'之间，实际 x={box!.CenterX:F3}");
        Assert.True(box.CenterY > 0.3 && box.CenterY < 0.4, $"合并中心 y≈0.35，实际 y={box.CenterY:F3}");
    }

    [Fact]
    public void FindBestSpan_无任何命中_返回高误差()
    {
        // FindBestSpan 的契约：候选非空时返回"最接近"的合并结果及其误差（可能 err=1.0），
        // 是否算命中由调用方用 maxErr 过滤（如 ScriptEngine 用 maxErr=0.3/0.5）。
        // "完全无关"与"参加"无任何包含/编辑相似 → 归一化编辑距离 = 1.0（完全无关）。
        var words = new List<RecognizedTextLine>
        {
            new("完全无关", 0.10, 0.10, 0.20, 0.10),
        };
        var (box, err) = TextMatcher.FindBestSpan("参加", words);
        Assert.NotNull(box);
        Assert.True(err >= 1.0, $"完全无关词应返回最大误差，实际 err={err:F3}");
    }
}
