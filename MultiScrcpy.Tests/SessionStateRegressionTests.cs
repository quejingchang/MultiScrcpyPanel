using System.Text;

using MultiScrcpy.Core;
using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 回归测试：会话态 / 扫描态的所有权边界，以及协议边界补漏。
/// <para>
/// 背景：<c>adb devices -l</c> 只能报出三态（device / unauthorized / offline），
/// 完全<b>无法感知</b>会话进展（Connecting / Streaming / Error）。
/// 因此 <see cref="DeviceInfo.MergeFrom"/> 若用扫描态无条件覆盖会话态，
/// 会把「正在投屏」降级成「已发现」，并连带打断依赖
/// <c>State == Streaming</c> 的下游功能（截图）。
/// </para>
/// </summary>
public class SessionStateRegressionTests
{
    // ---------------------------------------------------------------- 会话态所有权

    /// <summary>
    /// QA-BUG-01：投屏中的设备被扫描结果合并后，<b>必须仍然是 Streaming</b>。
    /// <para>
    /// 复现链路：<c>DeviceManager.ScanOnce()</c> → <c>old.MergeFrom(scanned)</c>
    /// → <c>DeviceInfo.State</c> 被改成 Detected → <c>DeviceSession.State</c>
    /// （<c>=&gt; Info.State</c>，共享同一实例）随之变成 Detected
    /// → <c>DeviceCard.OnScreenshot()</c> 的 <c>State != Streaming</c> 判定成立
    /// → 截图按钮永久失效。
    /// </para>
    /// </summary>
    [Fact]
    public void 扫描结果不得把投屏态降级为已发现态()
    {
        var live = new DeviceInfo("SN-STREAM", DeviceState.Streaming, "Pixel 6") { Battery = 80 };

        // adb devices -l 报 "device" → 扫描侧只会产出 Detected
        live.MergeFrom(new DeviceInfo("SN-STREAM", DeviceState.Detected));

        Assert.Equal(DeviceState.Streaming, live.State);
    }

    /// <summary>QA-BUG-01 衍生：Connecting（握手中）同样不能被扫描态打回 Detected。</summary>
    [Fact]
    public void 扫描结果不得把握手中降级为已发现态()
    {
        var connecting = new DeviceInfo("SN-CONN", DeviceState.Connecting);

        connecting.MergeFrom(new DeviceInfo("SN-CONN", DeviceState.Detected));

        Assert.Equal(DeviceState.Connecting, connecting.State);
    }

    /// <summary>QA-BUG-01 衍生：Error 是会话态，扫描到 device 不代表会话已恢复。</summary>
    [Fact]
    public void 扫描到device不得把错误态自动清成已发现态()
    {
        var failed = new DeviceInfo("SN-ERR", DeviceState.Error) { LastError = "握手超时" };

        failed.MergeFrom(new DeviceInfo("SN-ERR", DeviceState.Detected));

        Assert.Equal(DeviceState.Error, failed.State);
    }

    /// <summary>
    /// 反向边界：扫描到「掉线」是比会话态更权威的坏消息，<b>必须</b>覆盖。
    /// 否则设备拔线后卡片会永远停在「投影中」。
    /// </summary>
    [Fact]
    public void 扫描到掉线必须覆盖投屏态()
    {
        var live = new DeviceInfo("SN", DeviceState.Streaming);

        live.MergeFrom(new DeviceInfo("SN", DeviceState.Offline));

        Assert.Equal(DeviceState.Offline, live.State);
    }

    /// <summary>反向边界：扫描到「未授权」（用户撤销了授权）也必须覆盖会话态。</summary>
    [Fact]
    public void 扫描到未授权必须覆盖投屏态()
    {
        var live = new DeviceInfo("SN", DeviceState.Streaming);

        live.MergeFrom(new DeviceInfo("SN", DeviceState.Unauthorized));

        Assert.Equal(DeviceState.Unauthorized, live.State);
    }

    /// <summary>
    /// 非会话态（Detected / Unauthorized / Offline）之间，扫描结果照常覆盖 —— 
    /// 这是「重新授权成功后自动接入」链路的前提，不能被修复误伤。
    /// </summary>
    [Theory]
    [InlineData(DeviceState.Unauthorized, DeviceState.Detected)]   // 用户点了「允许」
    [InlineData(DeviceState.Offline, DeviceState.Detected)]        // 重新插上
    [InlineData(DeviceState.Detected, DeviceState.Unauthorized)]   // 撤销授权
    [InlineData(DeviceState.Detected, DeviceState.Offline)]        // 拔线
    public void 非会话态之间扫描结果照常覆盖(DeviceState from, DeviceState to)
    {
        var info = new DeviceInfo("SN", from);

        info.MergeFrom(new DeviceInfo("SN", to));

        Assert.Equal(to, info.State);
    }

    /// <summary>
    /// 端到端语义断言：连续两轮扫描后，投屏中的设备仍应满足「可截图」前置条件，
    /// 且型号 / 电量的合并语义不受修复影响。
    /// </summary>
    [Fact]
    public void 连续多轮扫描后投屏态与附加信息均稳定()
    {
        var live = new DeviceInfo("SN", DeviceState.Streaming, "Pixel 6") { Battery = 80 };

        for (int round = 0; round < 5; round++)
        {
            live.MergeFrom(new DeviceInfo("SN", DeviceState.Detected));
        }

        Assert.Equal(DeviceState.Streaming, live.State);   // 截图前置条件
        Assert.True(live.IsOnline());
        Assert.Equal("Pixel 6", live.Model);               // 空型号不抹掉
        Assert.Equal(80, live.Battery);                    // -1 不抹掉
    }

    // ---------------------------------------------------------------- 协议边界补漏

    /// <summary>
    /// 截断点落在 <b>3 字节</b> 汉字中间时同样要回退到码点起始处。
    /// 现有用例只覆盖了 4 字节 emoji，这里补 3 字节分支。
    /// </summary>
    [Fact]
    public void BuildText_截断点落在三字节汉字中间时回退到码点边界()
    {
        // 100 个 3 字节汉字 = 300 字节（恰好）；再加 1 个 → 303 字节，
        // 截断点 300 落在第 101 个汉字的首字节上，应恰好保留 300 字节。
        string exact = new('中', 100);
        byte[] m1 = ControlMessages.BuildText(exact + "中");
        byte[] payload1 = m1[ControlMessages.TEXT_HEADER_SIZE..];

        Assert.Equal(300, payload1.Length);
        Assert.Equal(exact, StrictUtf8().GetString(payload1));

        // 偏移 1 字节：'a' + 100 个汉字 = 301 字节，截断点 300 落在第 100 个汉字的
        // 第 3 字节上，必须回退到 298（= 1 + 3*99）。
        byte[] m2 = ControlMessages.BuildText("a" + exact);
        byte[] payload2 = m2[ControlMessages.TEXT_HEADER_SIZE..];

        Assert.Equal(298, payload2.Length);
        Assert.Equal("a" + new string('中', 99), StrictUtf8().GetString(payload2));
    }

    /// <summary>声明长度字段必须恒等于截断后的实际载荷长度（否则 server 侧会错位读包）。</summary>
    [Fact]
    public void BuildText_截断后声明长度必须等于实际载荷长度()
    {
        byte[] m = ControlMessages.BuildText(new string('中', 500));

        uint declared = (uint)((m[1] << 24) | (m[2] << 16) | (m[3] << 8) | m[4]);

        Assert.Equal((uint)(m.Length - ControlMessages.TEXT_HEADER_SIZE), declared);
        Assert.True(declared <= ScrcpyConstants.TEXT_MAX_LENGTH);
    }

    /// <summary>抛异常而非替换成 U+FFFD，用于证明载荷里没有半个码点。</summary>
    private static UTF8Encoding StrictUtf8() =>
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
