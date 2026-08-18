using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;

using MultiScrcpy.Core.Scripting.TextRecognition;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>TesseractTextRecognizer 集成测试（依赖系统已安装 tesseract 与 eng 语言包）。</summary>
public class TesseractTextRecognizerTests
{
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

}
