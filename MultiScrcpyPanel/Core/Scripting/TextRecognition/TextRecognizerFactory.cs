using System;
using System.Threading;

namespace MultiScrcpy.Core.Scripting.TextRecognition;

/// <summary>
/// 按配置创建 Tesseract 文字识别器。
/// <para>
/// 2026-08-19：已移除 Windows.Media.Ocr 路线（连同 WindowsMediaOcrTextRecognizer），
/// 统一走 Tesseract，完全照搬 D:\新建文件夹\OcrViewer 的 OCR 机制（OcrEngine.Recognize：stdout 纯文本）。
/// </para>
/// </summary>
public static class TextRecognizerFactory
{
    private static readonly Lazy<ITextRecognizer?> LazyDefault = new(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>默认识别器（懒加载，首次访问时探测 Tesseract 是否可用）。</summary>
    public static ITextRecognizer? Default => LazyDefault.Value;

    private static ITextRecognizer? CreateDefault()
    {
        try
        {
            var tesseract = new TesseractTextRecognizer();
            if (tesseract.IsAvailable)
            {
                return tesseract;
            }

            tesseract.Dispose();
        }
        catch
        {
            // Tesseract 不可用：返回 null
        }

        return null;
    }

    /// <summary>按配置创建识别器；cfg 为空或未知引擎时使用自动探测（仅 Tesseract）。</summary>
    public static ITextRecognizer? Create(AppConfig? cfg)
    {
        if (cfg == null)
        {
            return Default;
        }

        string engine = (cfg.OcrEngine ?? string.Empty).Trim().ToLowerInvariant();
        return engine switch
        {
            "windows" => TryCreateTesseract(cfg), // 2026-08-19：Windows 路线已移除，回退到 Tesseract
            "tesseract" => TryCreateTesseract(cfg),
            _ => Default
        };
    }

    private static ITextRecognizer? TryCreateTesseract(AppConfig cfg)
    {
        try
        {
            var tesseract = new TesseractTextRecognizer(
                string.IsNullOrWhiteSpace(cfg.TesseractPath) ? null : cfg.TesseractPath,
                string.IsNullOrWhiteSpace(cfg.OcrLanguage) ? "chi_sim+eng" : cfg.OcrLanguage,
                cfg.OcrTesseractPsm,
                cfg.OcrTesseractOem,
                cfg.OcrPreprocessScale,
                cfg.OcrGrayscale);
            if (tesseract.IsAvailable)
            {
                return tesseract;
            }

            tesseract.Dispose();
        }
        catch
        {
            // 显式指定 Tesseract 但不可用：返回 null
        }

        return null;
    }
}
