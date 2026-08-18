using System;
using System.IO;

using MultiScrcpy;
using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// QA 回归测试：锁定状态栏「正在扫描…」文本卡死的 Bug 根因 ——
/// <see cref="DeviceManager.ScanOnce"/> 在修复前<b>从不在扫描结束时把状态栏改写为终态</b>，
/// 导致一轮扫描跑完后 UI 仍停在「正在扫描…」，永不恢复。
/// <para>
/// 修复契约（本文件逐条上锁）：
/// <list type="number">
///   <item><description><see cref="DeviceManager"/> 新增事件
///   <see cref="DeviceManager.ScanCompleted"/>（参数：当前已知设备数、本轮是否出错）；</description></item>
///   <item><description>无论正常完成、adb 异常、还是中途 return（守卫），<c>ScanOnce</c> 的 <c>finally</c>
///   都会触发 <see cref="DeviceManager.ScanCompleted"/>，让 UI 把状态栏从「正在扫描…」改写为终态；</description></item>
///   <item><description>唯一<b>不</b>进 <c>finally</c> 的路径是 <c>_adb.IsAvailable == false</c> 的早返回
///   （位于 <c>try</c> 之前）—— 此路径下 MainForm 走启动错误红字分支、本就不显示「正在扫描」，故无缺口。</description></item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// 两个用例都只走<b>纯逻辑 + 进程启动即时失败</b>路径：不依赖系统 PATH、不启动真实 adb、不加载 WinForms 消息循环，
/// 可在 CI 无头环境直接 <c>dotnet test</c>。
/// <list type="bullet">
///   <item><description>用例 1：用一个注定启动失败的 adb 路径（绝对、不存在），<c>Process.Start</c> 立即抛
///   <see cref="AdbException"/> → 命中异常分支 → <c>finally</c> 触发 <see cref="DeviceManager.ScanCompleted"/>(0, true)；</description></item>
///   <item><description>用例 2：<c>IsAvailable == false</c> 的早返回路径在 <c>try</c> 之前，故<b>不</b>触发
///   <see cref="DeviceManager.ScanCompleted"/>，断言 <c>fired == false</c>。</description></item>
/// </list>
/// 若有人删掉 <c>finally</c> 里的 <c>ScanCompleted</c> 触发 → 用例 1 转红；
/// 若把早返回也补发信号 → 用例 2 转红。两条断言共同防止状态栏卡死回归。
/// </remarks>
[Trait("Category", "ADB")]
public sealed class ScanCompletedTests
{
    // ---------------------------------------------------------------- 用例 1：异常路径触发 + hadError=true

    /// <summary>
    /// 回归核心：adb 调用异常时，<c>ScanOnce</c> 必须<b>仍</b>触发
    /// <see cref="DeviceManager.ScanCompleted"/>，且参数为 <c>(count:0, hadError:true)</c>。
    /// 这正是「修复前状态栏卡死」的对立面 —— 修复后即便扫描失败，UI 也能收到终态信号退出「正在扫描…」。
    /// </summary>
    [Fact]
    public void 异常路径下ScanOnce触发ScanCompleted且hadError为true()
    {
        // 非空串 → IsAvailable==true，但文件路径注定不存在 → Process.Start 立即失败（不阻塞、不依赖 PATH）。
        var adb = new AdbClient(NonExistentAdbPath());
        Assert.True(adb.IsAvailable, "非空 adb 路径必须判定为可用，否则会走早返回分支而非异常分支。");

        var dm = new DeviceManager(new AppConfig(), adb);

        bool fired = false;
        (int count, bool hadError) payload = default;
        dm.ScanCompleted += (count, hadError) => { fired = true; payload = (count, hadError); };

        // 同步调用：ScanCompleted 在当前线程触发，调用返回前即可断言。
        dm.ScanOnce();

        Assert.True(fired, "异常路径必须触发 ScanCompleted，否则状态栏会永远卡在「正在扫描…」。");
        Assert.True(payload.hadError, "adb 进程启动失败属于异常，hadError 必须为 true。");
        Assert.Equal(0, payload.count); // 进程启动即失败，没有任何设备被解析出来。
    }

    // ---------------------------------------------------------------- 用例 2：IsAvailable=false 早返回不触发

    /// <summary>
    /// 反向回归：<c>IsAvailable == false</c> 的早返回位于 <c>try</c> 之前，<b>不</b>触发
    /// <see cref="DeviceManager.ScanCompleted"/>。此路径下 MainForm 已走启动错误红字分支、不显示「正在扫描」，
    /// 故不发送信号是正确的（避免 UI 把「早返回」误判成一次「空终态扫描」）。
    /// <para>若有人给早返回也补一句 <c>ScanCompleted?.Invoke(...)</c>，此断言立刻转红。</para>
    /// </summary>
    [Fact]
    public void adb未配置早返回时不触发ScanCompleted()
    {
        // 空串 → IsAvailable==false → 命中 try 之前的早返回。
        var adb = new AdbClient(string.Empty);
        Assert.False(adb.IsAvailable, "空 adb 路径必须判定为不可用，才能命中早返回分支。");

        var dm = new DeviceManager(new AppConfig(), adb);

        bool fired = false;
        dm.ScanCompleted += (_, _) => fired = true;

        dm.ScanOnce();

        Assert.False(fired, "IsAvailable==false 的早返回不应触发 ScanCompleted（尚未进入 try/finally）。");
    }

    /// <summary>构造一个绝对、必然不存在的 adb.exe 路径（不触碰真实文件系统写入）。</summary>
    /// <remarks>用绝对路径而非裸名，杜绝 PATH / 工作目录下误命中可执行文件，保证进程启动必然失败、断言不 flaky。</remarks>
    private static string NonExistentAdbPath()
        => Path.Combine(Path.GetTempPath(), "multiscrcpy-no-such-dir-7c91", "adb.exe");
}
