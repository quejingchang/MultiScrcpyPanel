using System;
using System.Drawing;
using System.Windows.Forms;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Protocol;
using MultiScrcpy.UI;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 临时探针：实测 <see cref="DeviceCard"/> 的 chrome 像素与横屏残余黑边（无句柄 vs 已创建句柄 vs 挂到 Form 上）。
/// </summary>
public sealed class QaChromeProbeTests
{
    private static readonly string OutPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qa_chrome_probe.txt");

    private static readonly double[] Scales = { 0.5, 0.75, 1.0, 1.5, 2.0 };

    private static readonly (int W, int H)[] Landscapes =
    {
        (1024, 472), (2400, 1080), (1920, 1080), (2560, 1080), (1024, 768), (1280, 800)
    };

    [Fact]
    public void Probe_Chrome像素与残余黑边()
    {
        if (System.IO.File.Exists(OutPath)) { System.IO.File.Delete(OutPath); }
        var cfg = new AppConfig();
        using var mgr = new DeviceManager(cfg, new AdbClient(string.Empty));

        DeviceInfo Info(string s, int w, int h) => new(s, DeviceState.Streaming, "QA") { VideoWidth = w, VideoHeight = h };

        Emit("======== ① chrome 像素：无句柄 vs 句柄 vs 真实 Form ========");

        using (var card = new DeviceCard(Info("no-handle", 1080, 2400), mgr))
        {
            card.PerformLayout();
            Emit($"[无句柄·构造后未 resize] card={S(card.Size)} clientSize={S(card.ClientSize)} screen={S(Find(card).Size)}");
        }

        using (var card = new DeviceCard(Info("no-handle-resized", 1080, 2400), mgr))
        {
            card.ApplyScale(1.0);
            card.Size = new Size(241, 601);
            card.Size = new Size(240, 600);   // 触发一次真实 resize
            card.PerformLayout();
            Emit($"[无句柄·resize 之后]     card={S(card.Size)} clientSize={S(card.ClientSize)} screen={S(Find(card).Size)}");
        }

        using (var card = new DeviceCard(Info("handle", 1080, 2400), mgr))
        {
            _ = card.Handle;
            card.PerformLayout();
            Emit($"[已创建句柄]             card={S(card.Size)} clientSize={S(card.ClientSize)} screen={S(Find(card).Size)}");
        }

        using (var form = new Form { Width = 1200, Height = 900 })
        using (var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true })
        {
            var card = new DeviceCard(Info("on-form", 1080, 2400), mgr);
            flow.Controls.Add(card);
            form.Controls.Add(flow);
            form.CreateControl();
            form.PerformLayout();
            Emit($"[挂真实 Form·运行态]     card={S(card.Size)} clientSize={S(card.ClientSize)} screen={S(Find(card).Size)}");
        }

        Emit($"[常量] ChromeWidth={DeviceCardLayout.ChromeWidth} ChromeHeight={DeviceCardLayout.ChromeHeight}");

        Emit(string.Empty);
        Emit("======== ② 运行态（句柄已建）横屏残余黑边矩阵 ========");
        Emit("device      scale  card        预测画面区   实测画面区   letterbox    左右黑边 上下黑边");

        int worstBar = 0;
        foreach ((int w, int h) in Landscapes)
        {
            foreach (double scale in Scales)
            {
                using var card = new DeviceCard(Info($"m-{w}x{h}-{scale}", w, h), mgr);
                _ = card.Handle;
                card.ApplyScale(scale);
                card.PerformLayout();

                ScreenView sv = Find(card);
                Size predicted = DeviceCardLayout.ComputeScreenArea(card.Size);
                Rectangle box = CoordinateMapper.ComputeLetterbox(sv.Width, sv.Height, w, h);
                int barX = sv.Width - box.Width;
                int barY = sv.Height - box.Height;
                worstBar = Math.Max(worstBar, Math.Max(barX, barY));

                Emit($"{w}x{h}".PadRight(12)
                     + scale.ToString("0.00").PadRight(7)
                     + S(card.Size).PadRight(12)
                     + S(predicted).PadRight(13)
                     + S(sv.Size).PadRight(13)
                     + $"{box.Width}x{box.Height}".PadRight(13)
                     + barX.ToString().PadRight(9)
                     + barY);
            }
        }

        Emit($"[运行态最大残余黑边] {worstBar}px");

        Emit(string.Empty);
        Emit("======== ③ 竖屏运行态 ========");
        foreach (double scale in Scales)
        {
            using var card = new DeviceCard(Info($"p-{scale}", 1080, 2400), mgr);
            _ = card.Handle;
            card.ApplyScale(scale);
            card.PerformLayout();
            ScreenView sv = Find(card);
            Rectangle box = CoordinateMapper.ComputeLetterbox(sv.Width, sv.Height, 1080, 2400);
            Emit($"scale={scale:0.00} card={S(card.Size)} 预测={S(DeviceCardLayout.ComputeScreenArea(card.Size))} 实测={S(sv.Size)} letterbox={box.Width}x{box.Height}");
        }

        Emit(string.Empty);
        Emit("======== ④ 假设 Chrome=4/94 修正后的横屏理论黑边 ========");
        int worstFixed = 0;
        foreach ((int w, int h) in Landscapes)
        {
            foreach (double scale in Scales)
            {
                Size card = FixedComputeCardSize(240, 600, scale, w, h);
                var img = new Size(card.Width - 4, card.Height - 94);
                Rectangle box = CoordinateMapper.ComputeLetterbox(img.Width, img.Height, w, h);
                worstFixed = Math.Max(worstFixed, Math.Max(img.Width - box.Width, img.Height - box.Height));
            }
        }

        Emit($"[修正后最大理论黑边] {worstFixed}px");

        Assert.True(true);
    }

    /// <summary>按 ChromeWidth=4 / ChromeHeight=94 重算的候选公式（仅供探针对照）。</summary>
    private static Size FixedComputeCardSize(int baseW, int baseH, double s, int videoW, int videoH)
    {
        const int cw = 4;
        const int ch = 94;
        int baseImgW = baseW - cw;
        int baseImgH = baseH - ch;
        int boxW = Math.Max(280 - ch, (int)Math.Round(baseImgH * s));
        int boxH = Math.Max(160 - cw, (int)Math.Round(baseImgW * s));
        double fit = Math.Min((double)boxW / videoW, (double)boxH / videoH);
        int imgW = Math.Max(1, (int)Math.Round(videoW * fit));
        int imgH = Math.Max(1, (int)Math.Round(videoH * fit));
        return new Size(imgW + cw, imgH + ch);
    }

    private static string S(Size s) => $"{s.Width}x{s.Height}";

    private static void Emit(string line)
    {
        System.IO.File.AppendAllText(OutPath, line + Environment.NewLine);
    }

    private static ScreenView Find(Control root)
    {
        ScreenView? sv = TryFind(root);
        return sv ?? throw new InvalidOperationException("ScreenView not found");
    }

    private static ScreenView? TryFind(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ScreenView sv)
            {
                return sv;
            }

            ScreenView? nested = TryFind(c);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }
}
