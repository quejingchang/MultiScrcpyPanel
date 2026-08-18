using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace MultiScrcpy.Core.Scripting.TextRecognition;

/// <summary>
/// 基于 Tesseract 命令行的真实文字识别实现。
/// <para>
/// 完全照搬 D:\新建文件夹\OcrViewer 的 OCR 机制（OcrEngine.Recognize）：
/// 1) 调用参数与 OcrViewer 完全一致：
///    "{imagePath}" stdout -l {lang} --oem {oem} --psm {psm}[-c tessedit_char_whitelist=...]；
/// 2) 直接读取 Tesseract 的<b>标准输出纯文本</b>（不再解析 TSV），从根本上消除 TSV 解析导致的乱码
///    （如 *(1.00)、}(1.00)、-1000=(1.00) 等），识别文本干净可靠；
/// 3) 预处理（放大 + 灰度）与 OcrViewer 的 Vision.Preprocess 默认参数完全一致
///    （Scale=2.0 Cubic 放大、Grayscale=true、不启用二值化/模糊/形态学）。
/// </para>
/// <para>
/// 为兼容现有 <see cref="ITextRecognizer.RecognizeAsync(Bitmap)"/> 接口，
/// 把纯文本结果按行包装为 <see cref="RecognizedTextLine"/> 列表：
/// 由于纯 stdout 模式不提供逐字坐标，每行按"整宽 0.8、左侧留 0.1、垂直均分"分配占位包围盒，
/// 仅用于让 OCR_TEXT 的"按文本命中并点击"逻辑有可点击区域；
/// 精确坐标仍建议用模板匹配（OCR/FIND）路线获取。
/// </para>
/// </summary>
public sealed class TesseractTextRecognizer : ITextRecognizer
{
    private readonly string? _exePath;
    private readonly string _language;
    private readonly int _psm;
    private readonly int _oem;
    private readonly double _scale;
    private readonly bool _grayscale;
    private readonly string _whitelist;

    /// <summary>创建 Tesseract 识别器。</summary>
    /// <param name="exePath">tesseract.exe 显式路径；为空则按显式路径 → 默认安装目录 → PATH → 程序目录顺序探测。</param>
    /// <param name="language">语言包，默认 chi_sim+eng。</param>
    /// <param name="psm">页面分割模式，默认 6（单一文本块）。</param>
    /// <param name="oem">OCR 引擎模式，默认 1（LSTM）。</param>
    /// <param name="scale">预处理放大倍数，默认 2.0；小于等于 1 视为不放大。</param>
    /// <param name="grayscale">是否先做灰度化，默认 true。</param>
    /// <param name="whitelist">字符白名单（tessedit_char_whitelist）；为空则不限制。</param>
    public TesseractTextRecognizer(
        string? exePath = null,
        string language = "chi_sim+eng",
        int psm = 6,
        int oem = 1,
        double scale = 2.0,
        bool grayscale = true,
        string? whitelist = null)
    {
        _exePath = FindTesseract(exePath);
        _language = string.IsNullOrWhiteSpace(language) ? "chi_sim+eng" : language;
        _psm = psm is >= 0 and <= 13 ? psm : 6;
        _oem = oem is >= 0 and <= 3 ? oem : 1;
        _scale = scale > 1.0 ? scale : 1.0;
        _grayscale = grayscale;
        _whitelist = whitelist ?? string.Empty;
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrEmpty(_exePath);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(Bitmap bitmap, CancellationToken token = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Tesseract 不可用：未找到 tesseract.exe。");
        }

        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        int srcW = bitmap.Width;
        int srcH = bitmap.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            return Array.Empty<RecognizedTextLine>();
        }

        string? tmp = null;
        try
        {
            // 预处理（对齐 OcrViewer Vision.Preprocess 默认：放大 2.0 + 灰度），结果写入临时 PNG。
            tmp = Path.Combine(Path.GetTempPath(), $"mscp_tess_{Guid.NewGuid():N}.png");
            using (Mat processed = PreprocessToMat(bitmap))
            {
                Cv2.ImWrite(tmp, processed);
            }

            // 调用 Tesseract，直接读取 stdout 纯文本（与 OcrViewer OcrEngine.Recognize 完全一致）。
            string stdout = await RunTesseractAsync(tmp, token);

            // 兼容 ITextRecognizer 接口：把纯文本按行包装为 RecognizedTextLine 列表。
            return WrapLines(stdout);
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); }
                catch { /* 临时文件清理失败不影响结果 */ }
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecognizedTextLine>> RecognizeWordsAsync(Bitmap bitmap, CancellationToken token = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Tesseract 不可用：未找到 tesseract.exe。");
        }

        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        int srcW = bitmap.Width;
        int srcH = bitmap.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            return Array.Empty<RecognizedTextLine>();
        }

        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"mscp_tessw_{Guid.NewGuid():N}.png");
            using (Mat processed = PreprocessToMat(bitmap))
            {
                Cv2.ImWrite(tmp, processed);
                int pw = processed.Width;
                int ph = processed.Height;

                // 用 tsv 输出格式获取每个词的真实包围盒（对齐 OcrViewer 不使用 TSV 纯文本，
                // 但模板内文字定位需要坐标，故此处改用 tsv；解析对编码与列数做了防御）。
                string tsv = await RunTesseractAsync(tmp, token, "tsv");
                var words = ParseTsvWords(tsv, pw, ph);

                // PSM 6（默认，整块文本）不适合游戏 UI：界面文字分散在不同位置，
                // PSM 6 经常漏识别。当默认 PSM 识别过少且未主动指定 PSM 11 时，
                // 自动用 PSM 11（sparse text，任意位置找尽可能多的文字）回退重试。
                if (words.Count < 3 && _psm != 11)
                {
                    string tsvSparse = await RunTesseractAsync(tmp, token, "tsv", psmOverride: 11);
                    var wordsSparse = ParseTsvWords(tsvSparse, pw, ph);
                    if (wordsSparse.Count > words.Count)
                    {
                        return wordsSparse;
                    }
                }

                return words;
            }
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); }
                catch { /* 临时文件清理失败不影响结果 */ }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // 无托管/非托管资源需要释放；保留以满足 ITextRecognizer 契约。
    }

    #region Tesseract 探测

    /// <summary>按显式路径 → 默认安装目录 → PATH → 程序目录顺序查找 tesseract.exe。</summary>
    private static string? FindTesseract(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string def = @"C:\Program Files\Tesseract-OCR\tesseract.exe";
        if (File.Exists(def))
        {
            return def;
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string cand = Path.Combine(dir.Trim().Trim('"'), "tesseract.exe");
                    if (File.Exists(cand))
                    {
                        return cand;
                    }
                }
                catch
                {
                    // PATH 中可能含非法路径，忽略
                }
            }
        }

        string local = Path.Combine(AppContext.BaseDirectory, "tesseract.exe");
        if (File.Exists(local))
        {
            return local;
        }

        return null;
    }

    #endregion

    #region 运行 Tesseract（对齐 OcrViewer OcrEngine.Recognize）

    private async Task<string> RunTesseractAsync(string imagePath, CancellationToken token, string outputConfig = "", int? psmOverride = null)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(imagePath).Append("\" stdout");
        sb.Append(" -l ").Append(_language);
        sb.Append(" --oem ").Append(_oem);
        sb.Append(" --psm ").Append(psmOverride ?? _psm);
        if (!string.IsNullOrWhiteSpace(_whitelist))
        {
            sb.Append(" -c tessedit_char_whitelist=").Append(_whitelist);
        }

        if (!string.IsNullOrWhiteSpace(outputConfig))
        {
            sb.Append(' ').Append(outputConfig);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using (token.Register(() =>
        {
            try { proc.Kill(); }
            catch { /* 进程已退出或无法终止 */ }
        }))
        {
            string stdout = await proc.StandardOutput.ReadToEndAsync(token);
            string stderr = await proc.StandardError.ReadToEndAsync(token);
            await proc.WaitForExitAsync(token);

            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                throw new InvalidOperationException($"Tesseract 执行失败：{stderr}");
            }

            return stdout;
        }
    }

    #endregion

    #region 预处理（对齐 OcrViewer Vision.Preprocess 默认参数）

    /// <summary>Bitmap → Mat（PNG 字节流 + ImDecode，与 OcrViewer 读图方式一致）→ 预处理。</summary>
    private Mat PreprocessToMat(Bitmap src)
    {
        Mat mat = BitmapToMat(src) ?? throw new InvalidOperationException("无法将 Bitmap 转为 Mat。");
        try
        {
            return Preprocess(mat);
        }
        finally
        {
            mat.Dispose();
        }
    }

    /// <summary>
    /// 对齐 OcrViewer Vision.Preprocess 默认行为：
    /// Grayscale=true（转灰度）→ Scale=2.0（Cubic 放大）→ 不启用二值化/模糊/形态学（与 Models.PreprocessSettings 默认一致）。
    /// </summary>
    private Mat Preprocess(Mat src)
    {
        Mat m = src.Clone();
        try
        {
            if (_grayscale && m.Channels() != 1)
            {
                Mat gray = ToGray(m);
                m.Dispose();
                m = gray;
            }

            if (_scale != 1.0)
            {
                Mat resized = m.Resize(new OpenCvSharp.Size(0, 0), _scale, _scale, InterpolationFlags.Cubic);
                m.Dispose();
                m = resized;
            }

            // OcrViewer 默认 Binarize=None、Blur=None、Morph=None，此处不做额外处理。
            // 末尾将单通道转回 3 通道（与 OcrViewer 行为一致），Tesseract 均可解析。
            if (m.Channels() == 1)
            {
                Mat bgr = m.CvtColor(ColorConversionCodes.GRAY2BGR);
                m.Dispose();
                m = bgr;
            }

            return m;
        }
        catch
        {
            m.Dispose();
            throw;
        }
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

    /// <summary>把 Bitmap 编码为 PNG 字节流后用 Cv2.ImDecode(Unchanged) 解码，与 OcrViewer 读图方式一致并保留 alpha。</summary>
    private static Mat? BitmapToMat(Bitmap bitmap)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            byte[] bytes = ms.ToArray();
            Mat decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
            return decoded != null && !decoded.Empty() ? decoded : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region 结果包装（纯文本 → RecognizedTextLine 列表）

    /// <summary>
    /// 把 Tesseract 的 stdout 纯文本按行包装为 <see cref="RecognizedTextLine"/> 列表。
    /// <para>
    /// 纯 stdout 模式不提供逐字坐标，这里为每行分配占位包围盒：
    /// 整宽 0.8、左侧留 0.1；各行按垂直方向均分（避开相邻行重叠），
    /// 保证 X&gt;0、Y&gt;0、Right≤1、Bottom≤1，且每行都有可点击区域。
    /// </para>
    /// </summary>
    private static IReadOnlyList<RecognizedTextLine> WrapLines(string stdout)
    {
        var result = new List<RecognizedTextLine>(8);
        if (string.IsNullOrEmpty(stdout))
        {
            return result;
        }

        var lines = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        int n = lines.Count;
        if (n == 0)
        {
            return result;
        }

        const double marginX = 0.1;
        const double width = 0.8;
        double bandH = 0.8 / n;
        for (int i = 0; i < n; i++)
        {
            double y = 0.1 + i * bandH;
            result.Add(new RecognizedTextLine(lines[i], marginX, y, width, bandH));
        }

        return result;
    }

    /// <summary>
    /// 解析 Tesseract 的 TSV 输出，提取 level==5（词）行的真实包围盒并归一化。
    /// <para>
    /// TSV 列（tab 分隔）：level / page_num / block_num / par_num / line_num / word_num /
    /// left / top / width / height / conf / text。坐标相对预处理后的图片尺寸，
    /// 而缩放对相对位置无影响，故归一化比例等同于相对原始模板图片的比例。
    /// </para>
    /// </summary>
    private static IReadOnlyList<RecognizedTextLine> ParseTsvWords(string tsv, int imgW, int imgH)
    {
        var result = new List<RecognizedTextLine>(8);
        if (string.IsNullOrWhiteSpace(tsv) || imgW <= 0 || imgH <= 0)
        {
            return result;
        }

        string[] lines = tsv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string raw in lines)
        {
            string[] cols = raw.Split('\t');
            if (cols.Length < 12)
            {
                continue;
            }

            // 仅取词级（level 5）行；页/块/行级行跳过。
            if (!int.TryParse(cols[0], out int level) || level != 5)
            {
                continue;
            }

            if (!int.TryParse(cols[6], out int left) || !int.TryParse(cols[7], out int top) ||
                !int.TryParse(cols[8], out int width) || !int.TryParse(cols[9], out int height))
            {
                continue;
            }

            if (width <= 0 || height <= 0)
            {
                continue;
            }

            string text = cols.Length > 11 ? cols[11] : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double cx = Clamp01((left + width / 2.0) / imgW);
            double cy = Clamp01((top + height / 2.0) / imgH);
            double w = Clamp01((double)width / imgW);
            double h = Clamp01((double)height / imgH);

            // RecognizedTextLine 以左上角 + 宽高表示，故由中心回推左上角。
            result.Add(new RecognizedTextLine(text, cx - w / 2.0, cy - h / 2.0, w, h));
        }

        return result;
    }

    /// <summary>把数值裁剪到 [0, 1]，避免 Tesseract 偶发的边界溢出。</summary>
    private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;

    #endregion
}
