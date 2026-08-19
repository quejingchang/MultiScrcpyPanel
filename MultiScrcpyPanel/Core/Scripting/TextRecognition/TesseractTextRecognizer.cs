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
/// <para>
/// 2026-08-18 三通道融合（仅 <see cref="RecognizeWordsAsync"/>）：
/// 通道 A 保留原"灰度 + 2x Cubic"行为；通道 B 额外做 Otsu 自适应二值化——游戏 UI
/// （深棕字 + 米色渐变 + 装饰边框）在二值化下对比度显著提升，可救回灰度通道完全失败的样本
/// （如"帮派任务"）、修复拆词（"使 用"）并去除渐变噪点；通道 C 取 HSV 色彩空间的 V（亮度）通道
/// ——彩色字/背景在 V 通道天然去色（如"橙黄按钮 + 深棕字"对比度被灰度化冲淡，V 通道仍保持高对比），
/// 可救回灰度与 Otsu 均救不回的彩色低对比样本（如"日常_宝图任务.png"的"参加"按钮）。
/// 三通道词列表按"文字内容相同 + 归一化中心距离 ≤ 0.02"合并去重，优先级 A &gt; B &gt; C；
/// 任一通道失败自动降级（不拖垮其他通道），全部失败才抛出首个异常。
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
    private readonly bool _enableBinaryChannel;

    /// <summary>创建 Tesseract 识别器。</summary>
    /// <param name="exePath">tesseract.exe 显式路径；为空则按显式路径 → 默认安装目录 → PATH → 程序目录顺序探测。</param>
    /// <param name="language">语言包，默认 chi_sim+eng。</param>
    /// <param name="psm">页面分割模式，默认 6（单一文本块）。</param>
    /// <param name="oem">OCR 引擎模式，默认 1（LSTM）。</param>
    /// <param name="scale">预处理放大倍数，默认 2.0；小于等于 1 视为不放大。</param>
    /// <param name="grayscale">是否先做灰度化，默认 true。</param>
    /// <param name="whitelist">字符白名单（tessedit_char_whitelist）；为空则不限制。</param>
    /// <param name="enableBinaryChannel">
    /// 是否启用 <see cref="RecognizeWordsAsync"/> 的附加通道（通道 B：Otsu 自适应二值化；
    /// 通道 C：HSV-V 亮度通道）识别，默认 true。置 false 可退回"单通道灰度"的旧行为
    /// （用于排查/对比）。两路附加通道互相独立、互不依赖，可独立失败降级。
    /// </param>
    public TesseractTextRecognizer(
        string? exePath = null,
        string language = "chi_sim+eng",
        int psm = 6,
        int oem = 1,
        double scale = 2.0,
        bool grayscale = true,
        string? whitelist = null,
        bool enableBinaryChannel = true)
    {
        _exePath = FindTesseract(exePath);
        _language = string.IsNullOrWhiteSpace(language) ? "chi_sim+eng" : language;
        _psm = psm is >= 0 and <= 13 ? psm : 6;
        _oem = oem is >= 0 and <= 3 ? oem : 1;
        _scale = scale > 1.0 ? scale : 1.0;
        _grayscale = grayscale;
        _whitelist = whitelist ?? string.Empty;
        _enableBinaryChannel = enableBinaryChannel;
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

        string? tmpA = null;
        string? tmpB = null;
        string? tmpC = null;
        try
        {
            Exception? firstError = null;
            bool anyChannelSucceeded = false;

            // 通道 A：灰度 + 2x Cubic 放大（完全保留原行为）。
            IReadOnlyList<RecognizedTextLine> wordsA = Array.Empty<RecognizedTextLine>();
            try
            {
                tmpA = Path.Combine(Path.GetTempPath(), $"mscp_tessw_{Guid.NewGuid():N}.png");
                using (Mat processed = PreprocessToMat(bitmap))
                {
                    Cv2.ImWrite(tmpA, processed);
                    wordsA = await RunWordsChannelAsync(tmpA, processed.Width, processed.Height, token);
                    anyChannelSucceeded = true;
                }
            }
            catch (Exception ex) when (IsDegradableChannelError(ex))
            {
                // 通道 A 降级：不影响附加通道的尝试；全部失败时由末尾的抛异常逻辑兜底。
                firstError ??= ex;
                Debug.WriteLine($"[OCR] 通道 A（灰度+2x）识别失败，降级：{ex.Message}");
                wordsA = Array.Empty<RecognizedTextLine>();
            }

            // 通道 B：灰度 + 2x Cubic 放大 + Otsu 自适应二值化（THRESH_BINARY + THRESH_OTSU）。
            // 游戏 UI（深棕字 + 米色渐变 + 装饰边框）在二值化下对比度显著提升，可救回灰度通道
            // 完全失败的样本（如"帮派任务"）、修复拆词（"使 用"）并去除渐变噪点；
            // 但二值化也会破坏部分干净模板（如"师门任务"），故必须与通道 A 融合而非一刀切。
            IReadOnlyList<RecognizedTextLine> wordsB = Array.Empty<RecognizedTextLine>();
            if (_enableBinaryChannel)
            {
                try
                {
                    tmpB = Path.Combine(Path.GetTempPath(), $"mscp_tessw_b_{Guid.NewGuid():N}.png");
                    using (Mat binary = BuildBinaryMat(bitmap))
                    {
                        Cv2.ImWrite(tmpB, binary);
                        wordsB = await RunWordsChannelAsync(tmpB, binary.Width, binary.Height, token);
                        anyChannelSucceeded = true;
                    }
                }
                catch (Exception ex) when (IsDegradableChannelError(ex))
                {
                    firstError ??= ex;
                    Debug.WriteLine($"[OCR] 通道 B（Otsu 二值化）识别失败，降级：{ex.Message}");
                    wordsB = Array.Empty<RecognizedTextLine>();
                }
            }

            // 通道 C：HSV-V 亮度通道 + 2x Cubic 放大（2026-08-18 新增）。
            // V 通道天然去色：所有彩色字在 V 通道都成深色、彩色背景成浅色；
            // "橙黄按钮 + 深棕字"（如"日常_宝图任务.png"的"参加"）在灰度化后对比被冲淡，
            // 但 V 通道仍能保持字/底高对比，可救回灰度与 Otsu 均失败的彩色低对比样本。
            // 失败自动降级：不影响通道 A/B 结果。
            IReadOnlyList<RecognizedTextLine> wordsC = Array.Empty<RecognizedTextLine>();
            if (_enableBinaryChannel)
            {
                try
                {
                    tmpC = Path.Combine(Path.GetTempPath(), $"mscp_tessw_c_{Guid.NewGuid():N}.png");
                    using (Mat value = BuildValueChannelMat(bitmap))
                    {
                        Cv2.ImWrite(tmpC, value);
                        wordsC = await RunWordsChannelAsync(tmpC, value.Width, value.Height, token);
                        anyChannelSucceeded = true;
                    }
                }
                catch (Exception ex) when (IsDegradableChannelError(ex))
                {
                    firstError ??= ex;
                    Debug.WriteLine($"[OCR] 通道 C（HSV-V 亮度）识别失败，降级：{ex.Message}");
                    wordsC = Array.Empty<RecognizedTextLine>();
                }
            }

            // 全部尝试过的通道都抛异常时，向上抛首个异常（保留主通道 A 的旧失败语义），
            // 避免静默返回空列表；至少有任一通道成功则进入合并（可能结果为空是合法情形）。
            if (!anyChannelSucceeded && firstError != null)
            {
                throw firstError;
            }

            return MergeAndDedupeWords(wordsA, wordsB, wordsC);
        }
        finally
        {
            // 三通道临时文件全清理（任一通道失败也不残留）。
            if (tmpA != null)
            {
                try { File.Delete(tmpA); }
                catch { /* 临时文件清理失败不影响结果 */ }
            }

            if (tmpB != null)
            {
                try { File.Delete(tmpB); }
                catch { /* 临时文件清理失败不影响结果 */ }
            }

            if (tmpC != null)
            {
                try { File.Delete(tmpC); }
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

    #region 三通道融合（通道 A 灰度 + 通道 B Otsu + 通道 C HSV-V）

    /// <summary>
    /// 对单张预处理图片跑 Tesseract TSV 词级识别。
    /// <para>
    /// 默认 PSM 6 时<b>始终</b>并行跑 PSM 6（整块文本）+ PSM 11（sparse text）并在通道内合并去重；
    /// 显式指定其他 PSM（含 11）时尊重调用方意图，只跑单 PSM、不回退。
    /// </para>
    /// <para>
    /// 2026-08-19 修复系统性丢字 Bug：旧逻辑"PSM 6 词数 &lt; 3 才回退 PSM 11"会导致
    /// 任何"目标文字只在 PSM 11 出现、PSM 6 已识别出其他词"的模板丢字
    /// （如"日常_师门任务.png"的"参加"：C 通道 PSM 6 命中 15 词不触发回退，
    /// 但"参加"仅在 PSM 11 出现）。改为始终双跑后，单侧 PSM 失败不拖垮另一侧
    /// （降级为仅保留成功侧结果），两测全失败才向上抛异常交由外层通道降级。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RecognizedTextLine>> RunWordsChannelAsync(
        string imagePath, int imgW, int imgH, CancellationToken token)
    {
        // 显式指定了非默认 PSM（11 或 7/8/12/13…）：单 PSM 模式，不做 PSM 6/11 双跑。
        if (_psm != 6)
        {
            string tsv = await RunTesseractAsync(imagePath, token, "tsv", psmOverride: _psm);
            return ParseTsvWords(tsv, imgW, imgH);
        }

        // 用 tsv 输出格式获取每个词的真实包围盒（对齐 OcrViewer 不使用 TSV 纯文本，
        // 但模板内文字定位需要坐标，故此处改用 tsv；解析对编码与列数做了防御）。
        // 默认 PSM 6：始终并行跑 PSM 6 + PSM 11（sparse text 找回分散文字），
        // 通道内合并去重，救回"只在 PSM 11 出现"的字（如"日常_师门任务.png"的"参加"）。
        Task<string> tsv6Task = RunTesseractAsync(imagePath, token, "tsv", psmOverride: 6);
        Task<string> tsv11Task = RunTesseractAsync(imagePath, token, "tsv", psmOverride: 11);
        try
        {
            await Task.WhenAll(tsv6Task, tsv11Task);
        }
        catch (Exception ex) when (IsDegradableChannelError(ex))
        {
            // 单侧 PSM 失败可降级：保留另一侧成功的结果；两测全失败时由下方兜底抛异常。
            Debug.WriteLine($"[OCR] 通道内 PSM 6/11 双跑部分失败，保留成功侧结果：{ex.Message}");
        }

        IReadOnlyList<RecognizedTextLine> words6 = Array.Empty<RecognizedTextLine>();
        IReadOnlyList<RecognizedTextLine> words11 = Array.Empty<RecognizedTextLine>();
        if (tsv6Task.IsCompletedSuccessfully)
        {
            words6 = ParseTsvWords(await tsv6Task, imgW, imgH);
        }

        if (tsv11Task.IsCompletedSuccessfully)
        {
            words11 = ParseTsvWords(await tsv11Task, imgW, imgH);
        }

        if (words6.Count == 0 && words11.Count == 0 && (tsv6Task.IsFaulted || tsv11Task.IsFaulted))
        {
            // 双跑全部失败：抛首个异常，由外层通道降级逻辑统一处理（保持"通道失败不拖垮其他通道"语义）。
            Exception? err = tsv6Task.Exception?.GetBaseException() ?? tsv11Task.Exception?.GetBaseException();
            throw err ?? new InvalidOperationException("Tesseract PSM 6/11 双跑均失败。");
        }

        return MergeAndDedupeWords(words6, words11);
    }

    /// <summary>
    /// 判定某通道的异常是否可降级（不拖垮其他通道继续尝试；取消异常必须传播给调用方）。
    /// <para>
    /// 涵盖：InvalidOperationException（Tesseract 自身执行失败/无可用语言包）、
    /// IOException / UnauthorizedAccessException（临时文件读写失败）、
    /// OpenCvSharpException（OpenCV 矩阵操作失败）、
    /// <see cref="System.ComponentModel.Win32Exception"/>（进程启动失败）。
    /// <see cref="OperationCanceledException"/> 不在此列，会正常向上传播以响应取消请求。
    /// </para>
    /// </summary>
    private static bool IsDegradableChannelError(Exception ex) =>
        ex is InvalidOperationException or IOException or UnauthorizedAccessException
            or OpenCvSharpException or System.ComponentModel.Win32Exception;

    /// <summary>
    /// Bitmap → 灰度 → 2x Cubic 放大 → Otsu 自适应二值化（THRESH_BINARY + THRESH_OTSU）。
    /// <para>
    /// Otsu 输出为单通道二值图，Tesseract 5 可直接解析，无需再转 BGR；
    /// 二值化提升游戏 UI（深棕字 + 米色渐变）对比度，作为通道 B 与灰度通道融合。
    /// </para>
    /// </summary>
    private Mat BuildBinaryMat(Bitmap src)
    {
        Mat mat = BitmapToMat(src) ?? throw new InvalidOperationException("无法将 Bitmap 转为 Mat。");
        try
        {
            Mat m = mat.Clone();
            try
            {
                if (m.Channels() != 1)
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

                Mat binary = new Mat();
                Cv2.Threshold(m, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                m.Dispose();
                return binary;
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }
        finally
        {
            mat.Dispose();
        }
    }

    /// <summary>
    /// Bitmap → BGR → HSV → 取 V 通道（亮度）→ 2x Cubic 放大。
    /// <para>
    /// V 通道天然去色：所有彩色字在 V 通道都成深色，所有彩色背景都成浅色——
    /// "橙黄按钮 + 深棕字"（如"日常_宝图任务.png"的"参加"）在灰度化后橙黄/深棕均成
    /// 中等灰度（~190/~80），对比度反被冲淡；而 V 通道让按钮成近白、文字成近黑，
    /// 对比度稳定提升，Otsu 类二值化在 V 图上对全图均稳定。
    /// 输出与 <see cref="BuildBinaryMat"/> 同形态的单通道 Mat（调用方负责 using 释放）。
    /// </para>
    /// </summary>
    private Mat BuildValueChannelMat(Bitmap src)
    {
        Mat mat = BitmapToMat(src) ?? throw new InvalidOperationException("无法将 Bitmap 转为 Mat。");
        try
        {
            Mat m = mat.Clone();
            try
            {
                // 单通道输入本身就是亮度通道（灰度），无需 HSV 转换，直接按 _scale 放大返回。
                if (m.Channels() == 1)
                {
                    if (_scale != 1.0)
                    {
                        Mat resized = m.Resize(new OpenCvSharp.Size(0, 0), _scale, _scale, InterpolationFlags.Cubic);
                        m.Dispose();
                        return resized;
                    }
                    return m;
                }

                // 4 通道（含 alpha）先转 BGR，保证 BGR2HSV 输入形态一致。
                if (m.Channels() == 4)
                {
                    Mat bgr = m.CvtColor(ColorConversionCodes.BGRA2BGR);
                    m.Dispose();
                    m = bgr;
                }

                // BGR → HSV → 取 V 通道（单通道 uint8）。
                Mat hsv = m.CvtColor(ColorConversionCodes.BGR2HSV);
                m.Dispose(); // 中间克隆已转成 HSV，立即释放 BGR 副本
                try
                {
                    Mat v = new Mat();
                    Cv2.ExtractChannel(hsv, v, 2); // V 通道（HSV 第 3 通道，coi=2）
                    try
                    {
                        if (_scale != 1.0)
                        {
                            Mat resized = v.Resize(new OpenCvSharp.Size(0, 0), _scale, _scale, InterpolationFlags.Cubic);
                            v.Dispose();
                            v = resized;
                        }
                        return v; // 调用方负责 using 释放
                    }
                    catch
                    {
                        v.Dispose();
                        throw;
                    }
                }
                finally
                {
                    hsv.Dispose();
                }
            }
            catch
            {
                m.Dispose();
                throw;
            }
        }
        finally
        {
            mat.Dispose();
        }
    }

    /// <summary>去重时判定"同一词"的归一化中心坐标距离阈值（0.02 ≈ 图片宽高的 2%）。</summary>
    internal const double DedupeCenterDistanceThreshold = 0.02;

    /// <summary>
    /// 合并两个通道的词列表并按"文字内容 + 归一化中心距离"去重。
    /// <para>
    /// 规则：两词 Text 相同（Ordinal）且归一化中心坐标距离 ≤ <see cref="DedupeCenterDistanceThreshold"/>
    /// 视为同一词，保留通道 A 的版本（避免通道 B 的轻微坐标偏移）。
    /// 通道 A 为空时直接返回通道 B（Otsu 可救回灰度完全失败的样本）。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<RecognizedTextLine> MergeAndDedupeWords(
        IReadOnlyList<RecognizedTextLine> wordsA,
        IReadOnlyList<RecognizedTextLine> wordsB)
    {
        if (wordsA.Count == 0)
        {
            return wordsB;
        }

        if (wordsB.Count == 0)
        {
            return wordsA;
        }

        var result = new List<RecognizedTextLine>(wordsA);
        foreach (RecognizedTextLine b in wordsB)
        {
            bool isDuplicate = false;
            foreach (RecognizedTextLine a in wordsA)
            {
                if (string.Equals(a.Text, b.Text, StringComparison.Ordinal) &&
                    CenterDistance(a, b) <= DedupeCenterDistanceThreshold)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                result.Add(b);
            }
        }

        return result;
    }

    /// <summary>
    /// 合并三个通道的词列表并按"文字内容 + 归一化中心距离"去重（用于三通道融合）。
    /// <para>
    /// 规则与两路合并一致（文字相同 + 中心距离 ≤ <see cref="DedupeCenterDistanceThreshold"/> 视为同一词），
    /// 优先级 A &gt; B &gt; C：A 命中不丢；B/C 命中且与 A 重复时保留 A 坐标；
    /// B 命中与 C 重复时保留 B 坐标。实现上复用两路合并做两次两两合并，结果等价（A+B 先合，再与 C 合）。
    /// 通道 A 为空时直接进入（A 空 → 返回 B），B 命中与 C 重复时走第二次两两合并，保留 B。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<RecognizedTextLine> MergeAndDedupeWords(
        IReadOnlyList<RecognizedTextLine> wordsA,
        IReadOnlyList<RecognizedTextLine> wordsB,
        IReadOnlyList<RecognizedTextLine> wordsC)
    {
        IReadOnlyList<RecognizedTextLine> merged = MergeAndDedupeWords(wordsA, wordsB);
        return MergeAndDedupeWords(merged, wordsC);
    }

    /// <summary>两词归一化中心坐标的欧氏距离。</summary>
    private static double CenterDistance(RecognizedTextLine a, RecognizedTextLine b)
    {
        double dx = a.CenterX - b.CenterX;
        double dy = a.CenterY - b.CenterY;
        return Math.Sqrt(dx * dx + dy * dy);
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
