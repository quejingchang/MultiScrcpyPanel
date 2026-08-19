using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;
using Xunit.Abstractions;

namespace MultiScrcpy.Tests;

/// <summary>
/// 43 张模板全量回归（QA 探针）：对比单通道（旧行为）/ 三通道（新行为）的词数与关键词命中，
/// 验证三通道融合不破坏单通道已识别的图（超集不变量）。
/// <para>结果通过 ITestOutputHelper 写到测试输出，可被 `dotnet test --logger "console;verbosity=detailed"` 捕获。</para>
/// </summary>
public class TemplateRegressionProbe
{
    private readonly ITestOutputHelper _output;

    public TemplateRegressionProbe(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>仓库 templates 目录绝对路径（支持中文路径）。</summary>
    private static string TemplatesDir
    {
        get
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\templates"),
                Path.Combine(Directory.GetCurrentDirectory(), "templates"),
            };
            foreach (string c in candidates)
            {
                string full = Path.GetFullPath(c);
                if (Directory.Exists(full))
                {
                    return full;
                }
            }

            return string.Empty;
        }
    }

    [Fact]
    public async Task T43模板全量回归_三通道vs单通道_超集不变量成立()
    {
        string dir = TemplatesDir;
        if (string.IsNullOrEmpty(dir))
        {
            _output.WriteLine("[SKIP] 未找到 templates 目录");
            return;
        }

        string[] templates = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToArray();
        _output.WriteLine($"[INFO] 共发现 {templates.Length} 张模板（目录: {dir}）");

        var single = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: false);
        var triple = new TesseractTextRecognizer(language: "chi_sim+eng", enableBinaryChannel: true);
        if (!single.IsAvailable || !triple.IsAvailable)
        {
            _output.WriteLine("[SKIP] Tesseract 不可用");
            return;
        }

        int totalSingle = 0, totalTriple = 0, lostCount = 0, expandedCount = 0;
        var perTemplate = new List<(string Name, int SingleN, int TripleN, int Added, bool Superset)>();
        var failures = new List<string>();

        foreach (string tpl in templates)
        {
            string name = Path.GetFileName(tpl);
            try
            {
                using var bmp = (System.Drawing.Image.FromFile(tpl) as System.Drawing.Bitmap)!;
                var wordsSingle = await single.RecognizeWordsAsync(bmp);
                var wordsTriple = await triple.RecognizeWordsAsync(bmp);
                int nS = wordsSingle.Count, nT = wordsTriple.Count;
                totalSingle += nS;
                totalTriple += nT;

                // 超集不变量：三通道结果必须包含单通道全部词（按 Text 集合判断；中心距离已在合并时去重）
                var setS = new HashSet<string>(wordsSingle.Select(w => w.Text));
                var setT = new HashSet<string>(wordsTriple.Select(w => w.Text));
                bool superset = setS.IsSubsetOf(setT);
                int added = setT.Count - (setS.IsSubsetOf(setT) ? setS.Count : setS.Intersect(setT).Count());

                if (!superset)
                {
                    lostCount++;
                    failures.Add($"{name}: 单通道 {nS} 词中丢失 {setS.Count - setS.Intersect(setT).Count()} 词 ({string.Join(",", setS.Except(setT).Take(5))})");
                }

                if (nT > nS)
                {
                    expandedCount++;
                }

                perTemplate.Add((name, nS, nT, added, superset));
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: 异常 {ex.GetType().Name} - {ex.Message}");
            }
        }

        // 报告汇总
        _output.WriteLine("");
        _output.WriteLine("===== 汇总 =====");
        _output.WriteLine($"模板总数:    {templates.Length}");
        _output.WriteLine($"成功扫描:    {perTemplate.Count}");
        _output.WriteLine($"单通道总词:  {totalSingle}");
        _output.WriteLine($"三通道总词:  {totalTriple}");
        _output.WriteLine($"三通道词数 > 单通道: {expandedCount}/{perTemplate.Count}");
        _output.WriteLine($"超集不变量违反: {lostCount}/{perTemplate.Count}");
        if (failures.Count > 0)
        {
            _output.WriteLine("");
            _output.WriteLine("===== 失败明细 =====");
            foreach (string f in failures)
            {
                _output.WriteLine(f);
            }
        }

        // 代表性子集报告（与主理人点名的 5-8 张对齐）
        string[] representative = new[]
        {
            "日常_宝图任务.png",
            "日常_三界奇缘.png",
            "日常_捉鬼任务.png",
            "日常_师门任务.png",
            "挑战_蜃影秘境.png",
            "按钮_使用.png",
            "按钮_参加.png",
            "按钮_确定.png",
            "导航_活动按钮.png",
        };
        _output.WriteLine("");
        _output.WriteLine("===== 代表性模板（与主理人 5-8 张点名单对齐） =====");
        _output.WriteLine("| 模板 | 单通道词数 | 三通道词数 | 新增 | 超集 |");
        _output.WriteLine("|------|-----------|-----------|------|------|");
        foreach (string n in representative)
        {
            var row = perTemplate.FirstOrDefault(r => r.Name == n);
            if (row.Name == null)
            {
                _output.WriteLine($"| {n} | (未找到) | | | |");
            }
            else
            {
                _output.WriteLine($"| {row.Name} | {row.SingleN} | {row.TripleN} | +{row.Added} | {(row.Superset ? "✓" : "✗")} |");
            }
        }

        // 全部 43 模板明细（完整表）
        _output.WriteLine("");
        _output.WriteLine("===== 全部模板明细 =====");
        _output.WriteLine("| 模板 | 单通道词数 | 三通道词数 | 新增 | 超集 |");
        _output.WriteLine("|------|-----------|-----------|------|------|");
        foreach (var r in perTemplate)
        {
            _output.WriteLine($"| {r.Name} | {r.SingleN} | {r.TripleN} | +{r.Added} | {(r.Superset ? "✓" : "✗")} |");
        }

        // 关键关键词命中检查
        _output.WriteLine("");
        _output.WriteLine("===== 关键关键词命中（FindBestSpan Error ≤ 0.5 视为命中） =====");
        var keyWords = new Dictionary<string, string[]>
        {
            { "日常_宝图任务.png", new[] { "参加" } },
            { "日常_三界奇缘.png", new[] { "帮派任务" } },
            { "日常_捉鬼任务.png", new[] { "捉鬼任务", "捉" } },
            { "日常_师门任务.png", new[] { "师门任务", "师门" } },
            { "挑战_蜃影秘境.png", new[] { "秘境" } },
            { "按钮_使用.png", new[] { "使用", "使" } },
            { "按钮_参加.png", new[] { "参加", "参" } },
            { "按钮_确定.png", new[] { "确定" } },
            { "导航_活动按钮.png", new[] { "活动" } },
        };
        foreach (var kv in keyWords)
        {
            string tpl = Path.Combine(dir, kv.Key);
            if (!File.Exists(tpl))
            {
                _output.WriteLine($"  {kv.Key}: (模板不存在)");
                continue;
            }

            using var bmp = (System.Drawing.Image.FromFile(tpl) as System.Drawing.Bitmap)!;
            var ws = await single.RecognizeWordsAsync(bmp);
            var wt = await triple.RecognizeWordsAsync(bmp);
            _output.WriteLine($"  {kv.Key}：");
            foreach (string kw in kv.Value)
            {
                var sSpan = TextMatcher.FindBestSpan(kw, ws);
                var tSpan = TextMatcher.FindBestSpan(kw, wt);
                string sHit = sSpan.Error <= 0.5 ? $"单通道✓({sSpan.Error:F2})" : $"单通道✗({sSpan.Error:F2})";
                string tHit = tSpan.Error <= 0.5 ? $"三通道✓({tSpan.Error:F2})" : $"三通道✗({tSpan.Error:F2})";
                _output.WriteLine($"  [{kw}] {sHit}→{tHit}");
            }
        }

        // 核心断言：所有模板超集不变量成立
        Assert.Equal(0, lostCount);
    }
}
