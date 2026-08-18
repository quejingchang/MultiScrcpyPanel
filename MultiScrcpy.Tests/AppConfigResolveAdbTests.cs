using System;
using System.IO;

using MultiScrcpy;
using MultiScrcpy.Core;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 回归测试：锁定 <see cref="AppConfig.ResolveAdb"/> 在「adb 缺失」场景下的<b>报错文案契约</b>。
/// <para>
/// 这是「adb 缺失提示不友好」Bug 的上游环节：<c>ResolveAdb</c> 负责在启动期就把
/// 「找不到 adb」翻译成一条能自救的中文提示，而不是让空路径一路漏到
/// <c>Process.Start</c> 变成英文 Win32Exception。修复后新增了
/// <c>{AppContext.BaseDirectory}\adb.exe</c> 的兜底探测（免 PATH 部署）。
/// </para>
/// </summary>
/// <remarks>
/// 除显式标注的「环境自适应」用例外，其余断言全部走
/// <c>AdbPath</c> 显式配置分支 —— 该分支在读取系统 PATH<b>之前</b>就返回或抛出，
/// 因此结果与机器上是否安装 adb 完全无关，可在 CI 无头环境稳定复现。
/// </remarks>
[Trait("Category", "ADB")]
public sealed class AppConfigResolveAdbTests
{
    // ---------------------------------------------------------------- 显式 AdbPath 分支（确定性）

    /// <summary>
    /// 配置里写了 adb 路径但文件不存在时，必须抛 <see cref="AdbException"/> 且明说「adb 不存在」，
    /// 不能静默回退到 PATH 查找 —— 否则用户改错了路径却毫无察觉。
    /// </summary>
    [Fact]
    public void 配置的adb路径不存在时_抛AdbException并提示adb不存在()
    {
        var cfg = new AppConfig { AdbPath = @"C:\no\such\adb.exe" };

        AdbException ex = Assert.Throws<AdbException>(() => cfg.ResolveAdb());

        Assert.Contains("adb 不存在", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>报错必须回显用户配错的那个路径，否则排查时不知道该改哪一行。</summary>
    [Fact]
    public void 配置的adb路径不存在时_报错回显出错的路径()
    {
        const string badPath = @"C:\no\such\adb.exe";
        var cfg = new AppConfig { AdbPath = badPath };

        AdbException ex = Assert.Throws<AdbException>(() => cfg.ResolveAdb());

        Assert.Contains(badPath, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 显式配置分支优先级最高：即便本机 PATH 里装着 adb，配错的 <c>AdbPath</c> 也必须直接报错。
    /// 这保证了上面两条断言在任何机器上都不 flaky。
    /// </summary>
    [Theory]
    [InlineData(@"C:\no\such\adb.exe")]
    [InlineData(@"D:\绝对不存在的目录\adb.exe")]
    [InlineData(@".\不存在的相对路径\adb.exe")]
    public void 配置分支不受系统PATH影响一律抛异常(string badPath)
    {
        var cfg = new AppConfig { AdbPath = badPath };

        Assert.Throws<AdbException>(() => cfg.ResolveAdb());
    }

    /// <summary>
    /// 反向用例：<c>AdbPath</c> 指向真实存在的文件时必须正常返回<b>绝对路径</b>，
    /// 防止有人为了修 Bug 把这一分支也改成无条件抛异常。
    /// </summary>
    [Fact]
    public void 配置的adb路径存在时_返回绝对路径且不抛异常()
    {
        string dir = Path.Combine(Path.GetTempPath(), "multiscrcpy-resolveadb-" + Guid.NewGuid().ToString("N"));
        string fake = Path.Combine(dir, "adb.exe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(fake, "not a real adb, only a path placeholder");

        try
        {
            var cfg = new AppConfig { AdbPath = fake };

            string resolved = cfg.ResolveAdb();

            Assert.True(Path.IsPathRooted(resolved), $"ResolveAdb 必须返回绝对路径，实际：{resolved}");
            Assert.True(File.Exists(resolved), $"ResolveAdb 返回的路径必须真实存在：{resolved}");
            Assert.Equal(Path.GetFullPath(fake), resolved);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    // ---------------------------------------------------------------- PATH 探测分支（环境自适应，非 flaky）

    /// <summary>
    /// <c>AdbPath</c> 为空时走「PATH → 程序目录」探测。本用例<b>两个分支都写死了断言</b>，
    /// 因此无论测试机是否安装 adb 都不会 flaky：
    /// <list type="bullet">
    ///   <item><description>环境里有 adb → 必须返回一个真实存在的绝对路径（<b>不能</b>是裸名 <c>"adb"</c>）；</description></item>
    ///   <item><description>环境里没有 adb → 必须抛 <see cref="AdbException"/>，且文案给全三条自救路径。</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public void 未配置adb路径时_要么返回真实存在的绝对路径_要么给出带安装指引的中文异常()
    {
        var cfg = new AppConfig();   // AdbPath 为空 → 走 PATH / 程序目录探测

        try
        {
            string resolved = cfg.ResolveAdb();

            // 本机确实装了 adb：必须是可直接 Process.Start 的真实路径，绝不能是裸名占位
            Assert.False(string.IsNullOrWhiteSpace(resolved));
            Assert.NotEqual("adb", resolved.Trim());
            Assert.True(File.Exists(resolved), $"ResolveAdb 返回的路径必须真实存在：{resolved}");
        }
        catch (AdbException ex)
        {
            // 本机没装 adb：这条文案就是用户唯一能看到的自救说明，逐条上锁
            Assert.Contains("请安装 Android Platform-Tools", ex.Message, StringComparison.Ordinal);
            Assert.Contains("PATH", ex.Message, StringComparison.Ordinal);
            Assert.Contains("程序目录", ex.Message, StringComparison.Ordinal);
            Assert.Contains("AdbPath", ex.Message, StringComparison.Ordinal);

            // 反向断言：不得再出现英文 Win32 噪声
            Assert.DoesNotContain("Win32", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
