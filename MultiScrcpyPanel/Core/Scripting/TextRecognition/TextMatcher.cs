using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MultiScrcpy.Core.Scripting.TextRecognition;

/// <summary>文字匹配辅助：支持精确/包含/编辑距离，并输出候选列表供诊断。</summary>
internal static class TextMatcher
{
    /// <summary>匹配一条候选及其与目标文本的误差。</summary>
    public readonly record struct Candidate(string Text, double Error);

    /// <summary>
    /// 在候选行中查找与目标文本最匹配的一项。
    /// 优先级：1) 候选包含目标（误差 0）；2) 目标包含候选（误差按长度差）；3) 归一化编辑距离。
    /// 这对中文游戏 UI 很有用——Windows OCR 常把相邻字词连成一个候选（如"师门任务参加"），
    /// 严格整词匹配会失败，而包含语义能命中。
    /// </summary>
    /// <param name="target">目标文本。</param>
    /// <param name="candidates">候选识别结果。</param>
    /// <param name="maxError">最大允许归一化编辑距离（0 = 完全相等，0.5 = 最多差一半字符）。</param>
    /// <param name="caseSensitive">是否区分大小写。</param>
    /// <returns>命中的行；未找到则返回 null。</returns>
    public static RecognizedTextLine? FindBest(string target, IEnumerable<RecognizedTextLine> candidates, double maxError, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        string t = Normalize(target, caseSensitive);
        RecognizedTextLine? best = null;
        double bestErr = double.MaxValue;

        foreach (RecognizedTextLine c in candidates)
        {
            string ct = Normalize(c.Text, caseSensitive);
            if (string.IsNullOrWhiteSpace(ct))
            {
                continue;
            }

            // 1) 候选里直接包含目标：最理想的命中（误差 0）
            if (ct.Contains(t, StringComparison.Ordinal))
            {
                best = c;
                bestErr = 0;
                continue;
            }

            // 2) 目标包含候选：误差按缺失字符比例（如目标"宝图任务" 候选"宝图"）
            double err;
            if (t.Contains(ct, StringComparison.Ordinal))
            {
                err = (double)(t.Length - ct.Length) / Math.Max(t.Length, 1);
            }
            else
            {
                err = NormalizedLevenshtein(t, ct);
            }

            if (err < bestErr)
            {
                bestErr = err;
                best = c;
            }
        }

        return best != null && bestErr <= maxError ? best : null;
    }

    /// <summary>
    /// 在候选词中查找能拼出目标文本的一组连续词，返回其合并包围盒与误差。
    /// <para>
    /// 这对中文 UI 尤其重要：Tesseract 常把"参加"拆成"参""加"两个词，
    /// 严格整词匹配会失败；本方法先做单字/子串扫描，再按阅读顺序（上→下、左→右）
    /// 尝试连续词合并，命中后返回覆盖所有组成词的并集包围盒，从而定位到正确的文字中心。
    /// </para>
    /// <para>
    /// 单字优先（BugFix）：当目标文字被某个<b>候选词本身</b>直接包含（err=0）时，
    /// 优先返回该词，不做多字合并。修复场景：Tesseract 把"运""镖""电""参加"识别为
    /// 独立词时，旧逻辑从阅读顺序靠前的"运"开始顺次合并出"运镖电参加"（仅包含目标），
    /// 其并集包围盒中心被无关前缀拉向模板中部，导致点击错位；本方法直接返回"参加"
    /// 单词的包围盒（短词优先，避免"运镖电参加"这类带无关前缀的长词）。
    /// </para>
    /// </summary>
    /// <returns>(合并包围盒, 误差)；未找到则返回 (null, +∞)。</returns>
    public static (RecognizedTextLine? Box, double Error) FindBestSpan(string target, IReadOnlyList<RecognizedTextLine> candidates)
    {
        if (string.IsNullOrWhiteSpace(target) || candidates.Count == 0)
        {
            return (null, double.MaxValue);
        }

        string t = Normalize(target, false);

        // 第一遍：单字/子串扫描。目标文字被某个候选词直接包含（err=0）时，
        // 直接返回该单词的包围盒，避免多字合并把无关前缀（如"运""镖""电"）并进来。
        // 排序：文本长度升序（短词优先——"参加"优于"运镖电参加"），
        // 同长度按阅读顺序（Y 升序、X 升序）保持确定性。
        RecognizedTextLine? direct = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => (Word: c, Norm: Normalize(c.Text, false)))
            .Where(x => x.Norm.Contains(t, StringComparison.Ordinal))
            .OrderBy(x => x.Norm.Length)
            .ThenBy(x => x.Word.Y)
            .ThenBy(x => x.Word.X)
            .Select(x => x.Word)
            .FirstOrDefault();

        if (direct != null)
        {
            return (direct, 0);
        }

        // 第二遍（兜底）：单字扫描无命中（err=0）时，才用"顺次扩展合并"逻辑
        // 兼容"参""加"被 Tesseract 拆成两个词等场景。
        // 按阅读顺序排序，保证连续合并符合视觉顺序。
        var ordered = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .OrderBy(c => c.Y)
            .ThenBy(c => c.X)
            .ToList();

        int n = ordered.Count;
        RecognizedTextLine? best = null;
        double bestErr = double.MaxValue;
        const int maxSpan = 6; // 单目标最多跨越的词数，防止误合并整行

        for (int i = 0; i < n; i++)
        {
            var sb = new StringBuilder();
            double minX = ordered[i].X;
            double minY = ordered[i].Y;
            double maxR = ordered[i].Right;
            double maxB = ordered[i].Bottom;

            for (int j = i; j < Math.Min(n, i + maxSpan); j++)
            {
                if (j > i)
                {
                    minX = Math.Min(minX, ordered[j].X);
                    minY = Math.Min(minY, ordered[j].Y);
                    maxR = Math.Max(maxR, ordered[j].Right);
                    maxB = Math.Max(maxB, ordered[j].Bottom);
                }

                sb.Append(ordered[j].Text);
                string comb = Normalize(sb.ToString(), false);

                double err;
                if (comb == t || comb.Contains(t, StringComparison.Ordinal))
                {
                    err = 0;
                }
                else if (t.Contains(comb, StringComparison.Ordinal))
                {
                    err = (double)(t.Length - comb.Length) / Math.Max(t.Length, 1);
                }
                else
                {
                    err = NormalizedLevenshtein(t, comb);
                }

                if (err < bestErr)
                {
                    bestErr = err;
                    double w = maxR - minX;
                    double h = maxB - minY;
                    best = new RecognizedTextLine(comb, minX, minY, w, h);
                }

                if (err == 0)
                {
                    break; // 已最优，无需继续扩展本起点
                }
            }

            if (bestErr == 0)
            {
                break;
            }
        }

        return (best, bestErr);
    }

    /// <summary>
    /// 返回按误差排序的前 N 个候选（用于日志诊断）。
    /// 误差计算与 <see cref="FindBest"/> 一致：包含 → 0；目标包含候选 → 长度差比例；否则编辑距离。
    /// </summary>
    public static IReadOnlyList<Candidate> TopCandidates(string target, IEnumerable<RecognizedTextLine> candidates, int topN, bool caseSensitive = false)
    {
        if (string.IsNullOrWhiteSpace(target) || topN <= 0)
        {
            return Array.Empty<Candidate>();
        }

        string t = Normalize(target, caseSensitive);
        var scored = new List<Candidate>();
        foreach (RecognizedTextLine c in candidates)
        {
            string ct = Normalize(c.Text, caseSensitive);
            if (string.IsNullOrWhiteSpace(ct))
            {
                continue;
            }

            double err;
            if (ct.Contains(t, StringComparison.Ordinal))
            {
                err = 0;
            }
            else if (t.Contains(ct, StringComparison.Ordinal))
            {
                err = (double)(t.Length - ct.Length) / Math.Max(t.Length, 1);
            }
            else
            {
                err = NormalizedLevenshtein(t, ct);
            }

            scored.Add(new Candidate(c.Text, err));
        }

        return scored.OrderBy(c => c.Error).ThenBy(c => c.Text).Take(topN).ToList();
    }

    private static string Normalize(string s, bool caseSensitive)
    {
        s = s.Trim();
        return caseSensitive ? s : s.ToLowerInvariant();
    }

    private static double NormalizedLevenshtein(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 0;
        }

        int distance = Levenshtein(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return maxLen == 0 ? 0 : (double)distance / maxLen;
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }
}
