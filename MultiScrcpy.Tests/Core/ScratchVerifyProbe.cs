using System;
using System.Drawing;
using System.Drawing.Imaging;

using MultiScrcpy.Core.Decoder;

using Xunit;
using Xunit.Abstractions;

namespace MultiScrcpy.Tests.Core;

/// <summary>临时探针：验证 Verify 工具的 1:1 触发条件与 de-Quantize 边界。用完即删。</summary>
public class ScratchVerifyProbe
{
    private readonly ITestOutputHelper _out;

    public ScratchVerifyProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe()
    {
        // 1) 穷举 --size WxW，找出所有会让 Quantize(W/2) == W（即 1:1 未缩放）的 W
        for (int w = 1; w <= 4096; w++)
        {
            if (FrameConverter.Quantize(w / 2) == w)
            {
                _out.WriteLine($"[1:1 触发] W={w} → Quantize({w / 2})={FrameConverter.Quantize(w / 2)}");
            }
        }

        // 2) 去掉 Quantize 后（scaled = W/2）是否还会 1:1
        for (int w = 1; w <= 4096; w++)
        {
            if (Math.Max(1, w / 2) == w)
            {
                _out.WriteLine($"[去Quantize后仍1:1] W={w}");
            }
        }

        // 3) 去掉 Quantize 后，new Bitmap(W/2, H/2) 的边界行为
        foreach (int w in new[] { 1, 2, 3 })
        {
            int scaled = w / 2;
            try
            {
                using var bmp = new Bitmap(scaled, scaled, PixelFormat.Format24bppRgb);
                _out.WriteLine($"[Bitmap] --size {w}x{w} → Bitmap({scaled},{scaled}) OK");
            }
            catch (Exception ex)
            {
                _out.WriteLine($"[Bitmap] --size {w}x{w} → Bitmap({scaled},{scaled}) 抛 {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
