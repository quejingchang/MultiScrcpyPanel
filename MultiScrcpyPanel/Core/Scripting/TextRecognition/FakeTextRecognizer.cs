using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace MultiScrcpy.Core.Scripting.TextRecognition;

/// <summary>用于单元测试的伪 OCR 识别器：按构造时注入的结果返回，不依赖系统 OCR。</summary>
public sealed class FakeTextRecognizer : ITextRecognizer
{
    private readonly List<RecognizedTextLine> _lines;

    public FakeTextRecognizer(params RecognizedTextLine[] lines)
    {
        _lines = new List<RecognizedTextLine>(lines);
    }

    public bool IsAvailable => true;

    public Task<IReadOnlyList<RecognizedTextLine>> RecognizeAsync(Bitmap bitmap, CancellationToken token = default)
    {
        return Task.FromResult<IReadOnlyList<RecognizedTextLine>>(_lines);
    }

    public Task<IReadOnlyList<RecognizedTextLine>> RecognizeWordsAsync(Bitmap bitmap, CancellationToken token = default)
    {
        return Task.FromResult<IReadOnlyList<RecognizedTextLine>>(_lines);
    }

    public void Dispose()
    {
    }
}
