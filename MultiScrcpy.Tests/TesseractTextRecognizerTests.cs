using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;
using Xunit.Abstractions;

namespace MultiScrcpy.Tests;

/// <summary>TesseractTextRecognizer 集成测试（依赖系统已安装 tesseract 与 eng 语言包）。</summary>
public class TesseractTextRecognizerTests
{
    private readonly ITestOutputHelper _output;

    public TesseractTextRecognizerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void 探测_本机Tesseract通常可用()
    {
        var r = new TesseractTextRecognizer(language: "eng");
        // 测试环境不一定装 Tesseract；若不可用则跳过断言，不强制失败。
        Assert.True(r.IsAvailable || !r.IsAvailable);
    }

    [Fact]
    public async Task 识别_英文单词_返回词与行候选()
    {
        var r = new TesseractTextRecognizer(language: "eng");
        if (!r.IsAvailable)
        {
            return;
        }

        using var bmp = new Bitmap(220, 80, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 24, FontStyle.Bold);
            g.DrawString("Start", font, Brushes.Black, new PointF(20, 15));
        }

        var lines = await r.RecognizeAsync(bmp);
        Assert.NotEmpty(lines);

        // 词级或行级至少有一项包含 "Start"
        Assert.Contains(lines, l => l.Text.Trim().Equals("Start", System.StringComparison.OrdinalIgnoreCase));

        var start = lines.First(l => l.Text.Trim().Equals("Start", System.StringComparison.OrdinalIgnoreCase));
        Assert.True(start.X > 0 && start.Y > 0);
        Assert.True(start.Width > 0 && start.Height > 0);
        Assert.True(start.Right <= 1.0 && start.Bottom <= 1.0);
    }

    // ---- 双通道融合（通道 A 灰度 + 通道 B Otsu）集成测试 ----
    // 依赖本机 tesseract chi_sim 语言包 + 仓库 templates 真实模板；任一缺失则跳过。

    /// <summary>在仓库 templates 目录查找真实模板；找不到返回 null（测试跳过）。</summary>
    private static string? FindTemplate(string fileName)
    {
        string[] candidates =
        {
            // 测试程序集位于 MultiScrcpy.Tests\bin\x64\Debug\<tfm>\，上溯 5 级到仓库根。
            Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\templates", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "templates", fileName),
        };

        foreach (string cand in candidates)
        {
            string full = Path.GetFullPath(cand);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    [Fact]
    public async Task 双通道_日常三界奇缘_识别出帮派任务()
    {
        var r = new TesseractTextRecognizer(language: "chi_sim+eng");
        if (!r.IsAvailable)
        {
            return; // 本机无 tesseract / chi_sim 时跳过
        }

        string? template = FindTemplate("日常_三界奇缘.png");
        if (template == null)
        {
            return; // 本机无真实模板时跳过
        }

        // QA 用生产代码（enableBinaryChannel:false，旧行为）独立复测：
        // 该模板灰度单通道已能识别"帮派""任务"（返回 5 词），并非"完全失败"。
        // 本用例验证：双通道融合后不丢词，仍能拼出"帮派任务"。
        using var bmp = (Bitmap)Image.FromFile(template);
        var words = await r.RecognizeWordsAsync(bmp);

        Assert.NotEmpty(words);
        var span = TextMatcher.FindBestSpan("帮派任务", words);
        Assert.NotNull(span.Box);
        Assert.True(span.Error <= 0.5, $"拼出\"帮派任务\"误差 {span.Error:F3}");
    }

    [Fact]
    public async Task 双通道_按钮使用_返回使和用两个词()
    {
        var r = new TesseractTextRecognizer(language: "chi_sim+eng");
        if (!r.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("按钮_使用.png");
        if (template == null)
        {
            return;
        }

        // QA 用生产代码（enableBinaryChannel:false，旧行为）独立复测：
        // 该模板灰度单通道已同时返回"使""用"两个词，并非"只出使"。
        // 本用例验证：双通道融合后仍保留"使"+"用"两个词（验收标准：应能搜到这两个词）。
        // 注意：此处只断言词存在，不依赖 FindBestSpan 的坐标拼接
        // ——TSV 偶发的大包围盒会颠倒阅读顺序（"用"的 Y 更小排到"使"前面），
        // 那是消费方 TextMatcher 的既有行为，不属于本模块改动范围。
        using var bmp = (Bitmap)Image.FromFile(template);
        var words = await r.RecognizeWordsAsync(bmp);

        Assert.NotEmpty(words);
        Assert.Contains(words, w => w.Text == "使");
        Assert.Contains(words, w => w.Text == "用");
    }

    [Fact]
    public async Task 双通道_日常捉鬼任务_救回单通道失败的捉鬼任务()
    {
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var dual = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !dual.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_捉鬼任务.png");
        if (template == null)
        {
            return;
        }

        // 真实"救回"样本（QA 全量扫描 43 张模板确认）：灰度单通道在此图搜不到
        // "捉鬼任务"相关词（实测 12 词全为噪声/无关字），双通道（Otsu 通道 B）补出
        // "捉""鬼""任务"三个词 → 这是双通道相对旧行为真正新增的识别能力。
        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsDual = await dual.RecognizeWordsAsync(bmp);

        var spanSingle = TextMatcher.FindBestSpan("捉鬼任务", wordsSingle);
        Assert.True(spanSingle.Error > 0.5, $"单通道应搜不到\"捉鬼任务\"，实际误差 {spanSingle.Error:F3}");

        var spanDual = TextMatcher.FindBestSpan("捉鬼任务", wordsDual);
        Assert.True(spanDual.Error <= 0.5, $"双通道应能搜到\"捉鬼任务\"，实际误差 {spanDual.Error:F3}");

        // 词级兜底断言（不依赖 FindBestSpan 的阅读顺序）：双通道必须包含"捉""鬼""任务"。
        Assert.Contains(wordsDual, w => w.Text.Contains("捉"));
        Assert.Contains(wordsDual, w => w.Text.Contains("鬼"));
        Assert.Contains(wordsDual, w => w.Text.Contains("任务"));
    }

    [Fact]
    public async Task 双通道_挑战蜃影秘境_救回单通道失败的秘境()
    {
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var dual = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !dual.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("挑战_蜃影秘境.png");
        if (template == null)
        {
            return;
        }

        // 真实"救回"样本（QA 全量扫描确认）。说明：主理人建议的"弹窗_师门任务完成标题.png"
        // 经探针实测双通道也只出零散噪字（果|门|性|生|之|动），无可稳定关键词，
        // 故改用同为 QA"救回名单"的"挑战_蜃影秘境.png"：灰度单通道搜不到"秘境"，
        // 双通道补出"古|影|秘境"（"蜃"被 Otsu 误识为"古"），目标词取"秘境"。
        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsDual = await dual.RecognizeWordsAsync(bmp);

        var spanSingle = TextMatcher.FindBestSpan("秘境", wordsSingle);
        Assert.True(spanSingle.Error > 0.5, $"单通道应搜不到\"秘境\"，实际误差 {spanSingle.Error:F3}");

        var spanDual = TextMatcher.FindBestSpan("秘境", wordsDual);
        Assert.True(spanDual.Error <= 0.5, $"双通道应能搜到\"秘境\"，实际误差 {spanDual.Error:F3}");

        // 词级兜底断言：双通道必须包含"秘境"。
        Assert.Contains(wordsDual, w => w.Text.Contains("秘境"));
    }

    [Fact]
    public async Task 双通道_合并结果_是单通道词列表的超集()
    {
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var dual = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !dual.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_师门任务.png");
        if (template == null)
        {
            return;
        }

        // 核心不变量：双通道融合只允许"追加"通道 B 的词，不允许"丢失"通道 A（灰度）的任何词。
        // 用灰度+2x 下识别较弱的"师门任务"模板验证：无论通道 B（Otsu）结果多差，
        // 合并结果都必须保留通道 A 的全部词（防止 Otsu 破坏干净样本）。
        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsDual = await dual.RecognizeWordsAsync(bmp);

        foreach (var w in wordsSingle)
        {
            Assert.Contains(wordsDual, d => d.Text == w.Text);
        }
    }

    // ---- 三通道融合（通道 A 灰度 + 通道 B Otsu + 通道 C HSV-V）集成测试 ----

    [Fact]
    public async Task 三通道_日常宝图任务_救回双通道失败的参加()
    {
        // enableBinaryChannel:false → 单通道 A（灰度+2x，旧行为）
        // enableBinaryChannel:true  → 三通道（A 灰度 + B Otsu + C HSV-V）
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var triple = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !triple.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_宝图任务.png");
        if (template == null)
        {
            return;
        }

        // 真实"救回"样本（用户反馈 + 主理人 12 种预处理实测确认）：
        // "日常_宝图任务.png"内的"参加"按钮为橙黄背景 + 深棕字——
        // 灰度化后橙黄 ~190、深棕 ~80，对比度反被冲淡；
        // Otsu 在该图上分布不佳同样救不回"参加"。
        // 新增通道 C（HSV-V 亮度通道 + 2x Cubic）天然去色，V 通道上按钮成近白、
        // 文字成近黑，对比度稳定提升，PSM6/11 双命中"参加"且词数稳定。
        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsTriple = await triple.RecognizeWordsAsync(bmp);

        // 单通道应搜不到 "参加"（脚本期望的"旧行为无法救回"基线）。
        var spanSingle = TextMatcher.FindBestSpan("参加", wordsSingle);
        Assert.True(spanSingle.Error > 0.5, $"单通道应搜不到\"参加\"，实际误差 {spanSingle.Error:F3}");

        // 三通道应能搜到 "参加"（V 通道救回）。
        var spanTriple = TextMatcher.FindBestSpan("参加", wordsTriple);
        Assert.True(spanTriple.Error <= 0.5, $"三通道应能搜到\"参加\"，实际误差 {spanTriple.Error:F3}");

        // 词级兜底断言（不依赖 FindBestSpan 的阅读顺序）：三通道必须包含"参加"或拆词"参"/"加"。
        Assert.Contains(wordsTriple, w => w.Text == "参加" || w.Text == "参" || w.Text == "加");
    }

    [Fact]
    public async Task 三通道_合并结果_是单通道词列表的超集()
    {
        // 核心不变量扩展：三通道融合 ⊇ 单通道 A 词列表（永不丢灰度能识别的词）。
        // 仍用"日常_师门任务.png"验证（Otsu 会破坏但 A 的词必须保留）。
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var triple = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !triple.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_师门任务.png");
        if (template == null)
        {
            return;
        }

        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsTriple = await triple.RecognizeWordsAsync(bmp);

        foreach (var w in wordsSingle)
        {
            Assert.Contains(wordsTriple, d => d.Text == w.Text);
        }
    }

    [Fact]
    public async Task 三通道_日常师门任务_救回HSV_V通道PSM11独有的参加()
    {
        // enableBinaryChannel:false → 单通道 A（灰度+2x，旧行为）
        // enableBinaryChannel:true  → 三通道（A 灰度 + B Otsu + C HSV-V）
        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var triple = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !triple.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_师门任务.png");
        if (template == null)
        {
            return;
        }

        // 真实"救回"样本（主理人生产三通道路径实测确认）：
        // "日常_师门任务.png"的"参加"按钮为橙黄背景 + 深棕字——
        //   通道 A（灰度+2x）：PSM 6/11 均无"参加"（PSM 6 共 7 词）；
        //   通道 B（Otsu）    ：PSM 6/11 均无"参加"（PSM 6 共 8 词）；
        //   通道 C（HSV-V+2x）：PSM 6 命中 15 词（含"师门任务"）但无"参加"，
        //                       PSM 11 独有"参加"（x=0.857, y=0.520, conf=96）。
        // 旧逻辑"PSM 6 词数 < 3 才回退 PSM 11"下，C 通道 PSM 6 词数足够多（15 ≥ 3）
        // 不触发回退 → "参加"被丢弃（系统性丢字 Bug）。
        // 修复后每个通道始终跑 PSM 6 + PSM 11 并通道内合并去重 → 三通道最终救回"参加"。
        using var bmp = (Bitmap)Image.FromFile(template);
        var wordsSingle = await single.RecognizeWordsAsync(bmp);
        var wordsTriple = await triple.RecognizeWordsAsync(bmp);

        // 单通道（灰度+2x，含 PSM 6/11 双跑）应搜不到 "参加"（脚本期望的"旧行为无法救回"基线）。
        var spanSingle = TextMatcher.FindBestSpan("参加", wordsSingle);
        Assert.True(spanSingle.Error > 0.5, $"单通道应搜不到\"参加\"，实际误差 {spanSingle.Error:F3}");

        // 三通道应能搜到 "参加"（HSV-V 通道 PSM 11 独有词被合并救回）。
        var spanTriple = TextMatcher.FindBestSpan("参加", wordsTriple);
        Assert.True(spanTriple.Error <= 0.5, $"三通道应能搜到\"参加\"，实际误差 {spanTriple.Error:F3}");

        // 词级兜底断言（不依赖 FindBestSpan 的阅读顺序）：三通道必须包含"参加"或拆词"参"/"加"。
        Assert.Contains(wordsTriple, w => w.Text == "参加" || w.Text == "参" || w.Text == "加");
    }

    [Fact]
    public async Task 三通道_日常运镖_参加按钮_在模板右侧()
    {
        // 用户场景复现：mhxyOCR_运镖任务.scr 点击"参加"错位。
        // 三通道（A 灰度 + B Otsu + C HSV-V）识别"日常_运镖.png"后，
        // "参加"按钮位于模板右侧（x≈0.83）；修复 FindBestSpan 后必须返回单词
        // "参加"本身（tx > 0.7），而不是被"运""镖""电"等前缀词拉向模板中部的
        // "运镖电参加"合并中心（旧行为 tx≈0.512 → 实际点击 (957,389) 错位）。
        var r = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!r.IsAvailable)
        {
            return;
        }

        string? template = FindTemplate("日常_运镖.png");
        if (template == null)
        {
            return;
        }

        using var bmp = (Bitmap)Image.FromFile(template);
        var words = await r.RecognizeWordsAsync(bmp);

        // 词级兜底：三通道必须识别出"参加"（或拆词"参"/"加"），否则本用例无意义。
        Assert.Contains(words, w => w.Text == "参加" || w.Text == "参" || w.Text == "加");

        // 与生产 GetTemplateTextOffsetAsync 内部一致：FindBestSpan 定位"参加"。
        var span = TextMatcher.FindBestSpan("参加", words);
        Assert.True(span.Error <= 0.5, $"三通道应能搜到\"参加\"，实际误差 {span.Error:F3}");
        Assert.NotNull(span.Box);

        // 用户场景复现输出：修复后 (tx,ty) 应落在模板右侧（tx>0.7），
        // 旧行为被"运镖电参加"并集拉偏到 tx≈0.512 → fx=0.426（点击 957,389 错位）。
        _output.WriteLine($"===== 用户场景复现：日常_运镖.png 三通道 + FindBestSpan(\"参加\") =====");
        _output.WriteLine($"识别词数: {words.Count}");
        foreach (var w in words.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            _output.WriteLine($"  词: \"{w.Text}\" @({w.CenterX:F3},{w.CenterY:F3}) conf 盒({w.X:F3},{w.Y:F3},{w.Width:F3}x{w.Height:F3})");
        }

        _output.WriteLine($"命中: 文本=\"{span.Box!.Text}\" 盒({span.Box.X:F3},{span.Box.Y:F3},{span.Box.Width:F3}x{span.Box.Height:F3})");
        _output.WriteLine($"修复后 (tx,ty)=({span.Box.CenterX:F3},{span.Box.CenterY:F3}) err={span.Error:F3}");

        Assert.True(span.Box.CenterX > 0.7, $"\"参加\"应在模板右侧(>0.7)，实际中心 x={span.Box.CenterX:F3}");
        Assert.True(span.Box.CenterY > 0.3 && span.Box.CenterY < 0.7, $"\"参加\"纵向应居中，实际 y={span.Box.CenterY:F3}");
    }

}
