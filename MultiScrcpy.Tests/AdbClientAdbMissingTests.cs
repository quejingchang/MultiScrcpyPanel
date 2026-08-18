using System;
using System.Diagnostics;
using System.IO;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 回归测试：锁定「adb 缺失提示不友好」Bug 的根因 ——
/// <c>AdbClient</c> 构造函数曾把空/为 null 的 <c>adbPath</c> 回退成裸名 <c>"adb"</c>，
/// 导致 <c>Process.Start</c> 抛出晦涩的英文 <see cref="System.ComponentModel.Win32Exception"/>
/// 「系统找不到指定的文件」，把「adb 根本没装」这一真实原因彻底掩盖。
/// <para>
/// 修复后的契约（本文件逐条上锁）：
/// <list type="number">
///   <item><description>构造允许 <c>adbPath</c> 为空，且<b>绝不</b>回退成裸名 <c>"adb"</c>；</description></item>
///   <item><description><see cref="AdbClient.IsAvailable"/> 把「未配置」暴露为可判定的布尔状态；</description></item>
///   <item><description><c>Run</c> / <c>SpawnShell</c> / <c>CreateStartInfo</c> 三处守卫统一抛
///   <see cref="AdbException"/>，文案为同一条中文提示（含「adb 未配置」）。</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// 全部断言只走<b>构造 + 状态判定 + 前置守卫</b>三条纯逻辑路径，守卫在 <c>Process.Start</c> 之前就抛出，
/// 因此不启动任何子进程、不依赖真机、不依赖系统 PATH 是否装有 adb，可在 CI 无头环境直接 <c>dotnet test</c>。
/// </remarks>
[Trait("Category", "ADB")]
public sealed class AdbClientAdbMissingTests
{
    /// <summary>守卫文案的关键词：用户一眼能看懂的中文，而不是英文 Win32 报错。</summary>
    private const string NotConfiguredKeyword = "adb 未配置";

    // ---------------------------------------------------------------- 构造 / IsAvailable

    /// <summary>构造传 null 时不得抛异常，且必须判定为「不可用」。</summary>
    [Fact]
    public void 构造传null时_IsAvailable为false()
    {
        var client = new AdbClient(null);

        Assert.False(client.IsAvailable);
    }

    /// <summary>构造传空串时必须判定为「不可用」。</summary>
    [Fact]
    public void 构造传空串时_IsAvailable为false()
    {
        var client = new AdbClient("");

        Assert.False(client.IsAvailable);
    }

    /// <summary>null / 空串 / 纯空白都属于「未配置」，不能有任何一种被误判成可用。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void 构造传空白路径时一律判定为不可用(string? adbPath)
    {
        Assert.False(new AdbClient(adbPath).IsAvailable);
    }

    /// <summary>
    /// 回归核心：空路径<b>绝不</b>被悄悄回退成裸名 <c>"adb"</c>。
    /// 若有人改回 <c>_adbPath = adbPath ?? "adb"</c>，此断言立刻转红。
    /// </summary>
    [Fact]
    public void 构造传null时AdbPath为空串而非回退成裸名adb()
    {
        var client = new AdbClient(null);

        Assert.Equal(string.Empty, client.AdbPath);
        Assert.NotEqual("adb", client.AdbPath);
    }

    /// <summary>
    /// 路径非空即视为「已配置」：<see cref="AdbClient.IsAvailable"/> 只做状态判定，
    /// 不做文件存在性检查（存在性由 <c>AppConfig.ResolveAdb</c> 负责），因此这里不会启动任何进程。
    /// </summary>
    [Fact]
    public void 构造传非空路径时_IsAvailable为true且不启动任何进程()
    {
        var client = new AdbClient(NonExistentAdbPath());

        Assert.True(client.IsAvailable);
        Assert.Equal(NonExistentAdbPath(), client.AdbPath);
    }

    // ---------------------------------------------------------------- Run 守卫

    /// <summary>未配置时 <c>Run</c> 必须抛 <see cref="AdbException"/>，且文案含「adb 未配置」。</summary>
    [Fact]
    public void 未配置时Run抛AdbException且提示adb未配置()
    {
        var client = new AdbClient(null);

        AdbException ex = Assert.Throws<AdbException>(() => { client.Run(new[] { "devices", "-l" }); });

        Assert.Contains(NotConfiguredKeyword, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>守卫文案必须给出两条自救路径：装 Platform-Tools 加 PATH，或在 settings.json 里配 AdbPath。</summary>
    [Fact]
    public void 未配置时Run的提示文案包含两条自救指引()
    {
        var client = new AdbClient("");

        AdbException ex = Assert.Throws<AdbException>(() => { client.Run(new[] { "devices", "-l" }); });

        Assert.Contains("Android Platform-Tools", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PATH", ex.Message, StringComparison.Ordinal);
        Assert.Contains("config/settings.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AdbPath", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反向断言：用户绝不能再看到英文 Win32 噪声。
    /// 守卫必须在 <c>Process.Start</c> 之前拦截，所以文案里不该出现 Win32 / 系统级英文错误串。
    /// </summary>
    [Fact]
    public void 未配置时的提示不含英文Win32噪声()
    {
        var client = new AdbClient(null);

        AdbException ex = Assert.Throws<AdbException>(() => { client.Run(new[] { "devices", "-l" }); });

        Assert.DoesNotContain("Win32", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot find the file", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("系统找不到指定的文件", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Devices()</c> 是 UI 扫描链路的实际入口（内部走 <c>Run</c>），
    /// 未配置时同样必须给出中文 <see cref="AdbException"/>，而不是让底层异常穿透。
    /// </summary>
    [Fact]
    public void 未配置时Devices走Run守卫同样抛AdbException()
    {
        var client = new AdbClient(null);

        AdbException ex = Assert.Throws<AdbException>(() => { client.Devices(); });

        Assert.Contains(NotConfiguredKeyword, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- SpawnShell 守卫

    /// <summary>未配置时 <c>SpawnShell</c> 必须抛 <see cref="AdbException"/>（而非 ServerLaunchException / Win32Exception）。</summary>
    [Fact]
    public void 未配置时SpawnShell抛AdbException且提示adb未配置()
    {
        var client = new AdbClient(null);

        AdbException ex = Assert.Throws<AdbException>(() =>
        {
            using Process p = client.SpawnShell("deadbeef", "echo regression-guard");
        });

        Assert.Contains(NotConfiguredKeyword, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 三处守卫共用同一条常量文案：<c>Run</c> 与 <c>SpawnShell</c> 的报错必须逐字一致。
    /// 若有人把其中一处改成自定义文案（或退回英文），此断言转红。
    /// </summary>
    [Fact]
    public void Run与SpawnShell的未配置文案完全一致()
    {
        var client = new AdbClient(null);

        string fromRun = Assert.Throws<AdbException>(() => { client.Run(new[] { "devices", "-l" }); }).Message;
        string fromSpawn = Assert.Throws<AdbException>(() =>
        {
            using Process p = client.SpawnShell("deadbeef", "echo regression-guard");
        }).Message;

        Assert.Equal(fromRun, fromSpawn);
    }

    // ---------------------------------------------------------------- 兜底：异常类型不外泄

    /// <summary>
    /// 已配置但文件不存在时（用户把 AdbPath 写错），<c>Process.Start</c> 会抛 Win32Exception，
    /// <c>Run</c> 必须把它包装成 <see cref="AdbException"/> 后再上抛，保证 UI 层只需 catch 一种异常。
    /// <para>路径取自系统临时目录下一个必然不存在的子目录，跨机器确定性成立。</para>
    /// </summary>
    [Fact]
    public void 路径写错时Run把底层异常包装成AdbException而非直接外泄()
    {
        var client = new AdbClient(NonExistentAdbPath());

        AdbException ex = Assert.Throws<AdbException>(() => { client.Run(new[] { "devices", "-l" }); });

        Assert.Contains("无法启动 adb 进程", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>构造一个必然不存在的 adb.exe 路径（不触碰真实文件系统写入）。</summary>
    private static string NonExistentAdbPath()
        => Path.Combine(Path.GetTempPath(), "multiscrcpy-no-such-dir-4f2a", "adb.exe");
}
