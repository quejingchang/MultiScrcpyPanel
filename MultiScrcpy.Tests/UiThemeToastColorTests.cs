using System.Drawing;

using MultiScrcpy.UI;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 回归测试：锁定「启动崩溃」Bug 的根因 —— <c>ToastForm</c> 构造函数里
/// <c>BackColor = UiTheme.ToastInfo</c> 触发
/// <see cref="System.ArgumentException"/>「控件不支持透明的背景色」。
/// <para>
/// 根因：<c>UiTheme.ToastInfo / ToastWarn / ToastError</c> 曾被写成
/// <c>Color.FromArgb(235, R, G, B)</c>（Alpha=235 半透明），而 WinForms 的
/// <c>Control.BackColor</c> 不允许 Alpha&lt;255。修复：改回
/// <c>Color.FromArgb(255, R, G, B)</c>（不透明），半透明由 <c>ToastForm.Opacity=0.92</c> 负责。
/// </para>
/// </summary>
/// <remarks>
/// 本测试只读取 <see cref="Color"/> 结构体的 <c>A</c> 属性，不实例化任何 WinForms 控件、
/// 不启动消息循环 / 不要求 STA 线程，因此可在 CI 无头环境直接 <c>dotnet test</c> 运行。
/// 若有人再次把 Alpha 改回 235，<c>ToastInfo.A == 255</c> 等断言即转红，阻止回归。
/// </remarks>
[Trait("Category", "UI")]
public class UiThemeToastColorTests
{
    /// <summary>ToastInfo 必须是不透明色（A==255），否则 ToastForm 构造函数赋 BackColor 会抛 ArgumentException。</summary>
    [Fact]
    public void ToastInfo_必须是不透明色_否则启动崩溃()
    {
        Assert.Equal(255, UiTheme.ToastInfo.A);
    }

    /// <summary>ToastWarn 必须是不透明色（A==255）。</summary>
    [Fact]
    public void ToastWarn_必须是不透明色_否则赋BackColor会抛异常()
    {
        Assert.Equal(255, UiTheme.ToastWarn.A);
    }

    /// <summary>ToastError 必须是不透明色（A==255）。</summary>
    [Fact]
    public void ToastError_必须是不透明色_否则赋BackColor会抛异常()
    {
        Assert.Equal(255, UiTheme.ToastError.A);
    }

    /// <summary>
    /// 覆盖 <c>ToastForm.Show()</c> 里 <c>BackColor = UiTheme.ToastColorFor(level)</c> 这条等效路径：
    /// 任意 ToastLevel 经 <see cref="UiTheme.ToastColorFor"/> 取出的底色都必须不透明（A==255）。
    /// </summary>
    [Theory]
    [InlineData(ToastLevel.Info)]
    [InlineData(ToastLevel.Warn)]
    [InlineData(ToastLevel.Error)]
    public void ToastColorFor_任意级别返回的底色都必须是不透明色(ToastLevel level)
    {
        Assert.Equal(255, UiTheme.ToastColorFor(level).A);
    }
}
