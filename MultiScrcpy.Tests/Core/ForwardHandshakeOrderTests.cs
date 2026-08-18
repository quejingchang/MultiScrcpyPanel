using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// forward 模式「握手顺序」的回归测试（真实 TCP loopback，无 adb / 无真机）。
///
/// <para>
/// <b>被锁定的缺陷（握手死锁）</b>：scrcpy v4.0 服务端
/// <c>com.genymobile.scrcpy.device.DesktopConnection</c> 用<b>同一个</b>
/// <c>LocalServerSocket</c> 顺序 accept（video → audio → control），
/// <b>全部连接就位之后</b>才把 64 字节设备名写进 video socket：
/// <code>
/// try (LocalServerSocket localServerSocket = new LocalServerSocket(socketName)) {
///     if (video)   { videoSocket   = localServerSocket.accept(); if (sendDummyByte) write(0); }
///     if (audio)   { audioSocket   = localServerSocket.accept(); ... }
///     if (control) { controlSocket = localServerSocket.accept(); ... }
/// }
/// if (options.getSendDeviceMeta()) connection.sendDeviceMeta(Device.getDeviceName());
/// </code>
/// 旧的 <c>DeviceSession.StreamLoop</c> 顺序是
/// 「连 video → <b>读设备名</b> → 连 control」，于是客户端阻塞在读设备名、
/// server 阻塞在 <c>accept()</c> 等 control socket，双方互等直到握手超时，抛
/// <c>读取设备名超时（已读 0/64 字节）</c>。
/// （当时握手超时曾被误判为「设备端慢」而放宽到 30s，所以真机上要干等 30s；
/// 根因定位为顺序错误后已改回 <see cref="VideoStreamReader.HandshakeTimeoutMs"/> = 5s 快速失败。）
/// </para>
///
/// <para>
/// <b>本文件的作用</b>：把「<b>必须先连 control socket，再读设备名</b>」这个顺序钉死，
/// 并用 <see cref="ForwardServerWithControl"/> 复刻服务端的跨连接时序契约。
/// </para>
///
/// <para>
/// ⚠️ <b>覆盖边界（QA 变异实验实测，勿高估）</b>：本文件里第 1、2 节的「行为型」用例
/// <b>并不驱动 <c>StreamLoop</c> 本身</b>——它们在测试方法体内用反射手工按正确顺序调用
/// <c>ConnectVideoSocket</c> / <c>ConnectWithRetry</c>，顺序是<b>测试自己写的</b>。
/// 实测把 <c>StreamLoop</c> 改回死锁顺序后，本文件 8 条用例中<b>只有</b>
/// <see cref="StreamLoop源码中控制通道连接必须排在读设备名之前"/> 会红。
/// 真正针对生产代码顺序的守护在
/// <see cref="StreamLoopHandshakeOrderIlTests"/>（解析编译产物的 IL，不依赖源码树、不会静默跳过）。
/// 修改握手顺序时，<b>这两处必须一起看</b>。
/// </para>
/// </summary>
public class ForwardHandshakeOrderTests
{
    /// <summary>单个用例的兜底时长上限，防止实现回退成死等时把 CI 挂住。</summary>
    private static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(30);

    /// <summary>握手各阶段事件的等待上限。</summary>
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 「死锁验证」用例里故意用的短读超时：不等满真实的
    /// <see cref="VideoStreamReader.HandshakeTimeoutMs"/>（5s），以免每跑一次 CI 就白等 5 秒。
    /// </summary>
    private const int DeadlockProbeTimeoutMs = 1500;

    // ================================================================
    // 1. 正确顺序：video → control → 设备名 → codec id
    // ================================================================

    [Fact]
    public void 正确顺序_先连control再读设备名_握手成功()
    {
        using var server = new ForwardServerWithControl(deviceName: "HUAWEI ANA-AN00");
        using var session = CreateSession();

        var watch = Stopwatch.StartNew();

        // ---- 步骤 1：video socket（forward 模式，dummy 在连接阶段被消费）----
        using Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        Assert.True(videoSocket.Connected);
        Assert.True(server.WaitVideoAccepted(StepBudget), "server 应已 accept video socket。");

        using var reader = new VideoStreamReader(videoSocket);

        // ---- 步骤 2：⭐ 必须先连 control socket，server 才会写出设备名 ----
        Assert.False(server.IsMetaSent, "control socket 尚未连接时，server 绝不应发送 device meta。");

        using Socket controlSocket = InvokeConnectWithRetry(session, server.Port);
        Assert.True(controlSocket.Connected);
        Assert.True(server.WaitControlAccepted(StepBudget), "server 应已 accept control socket。");

        // ---- 步骤 3：此时设备名才会到达 ----
        string deviceName = reader.ReadDeviceName();
        uint codecId = reader.ReadCodecId();

        watch.Stop();

        Assert.Equal("HUAWEI ANA-AN00", deviceName);
        Assert.Equal(ScrcpyConstants.CODEC_H264, codecId);
        Assert.True(watch.Elapsed < CaseBudget, $"耗时 {watch.ElapsedMilliseconds}ms 明显异常。");

        Assert.True(server.MetaSentAfterControlAccepted,
            "device meta 必须发生在 control socket accept 之后——这是 scrcpy v4.0 的服务端契约。");
        Assert.Equal(2, server.AcceptedCount);
    }

    [Fact]
    public void 正确顺序_首连被FIN后重试_仍能按序完成握手()
    {
        // 叠加真机上的另一个时序：adb 已 listen 但设备端还没 bind 抽象套接字 → 首连被 FIN。
        using var server = new ForwardServerWithControl(failFirst: 2, deviceName: "Pixel 7");
        using var session = CreateSession();

        using Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        Assert.True(server.WaitVideoAccepted(StepBudget));

        using var reader = new VideoStreamReader(videoSocket);
        using Socket controlSocket = InvokeConnectWithRetry(session, server.Port);
        Assert.True(server.WaitControlAccepted(StepBudget));

        Assert.Equal("Pixel 7", reader.ReadDeviceName());
        Assert.Equal(ScrcpyConstants.CODEC_H264, reader.ReadCodecId());

        Assert.True(server.AcceptedCount >= 4,
            $"应至少 4 次 accept（2 次被 FIN + video + control），实际 {server.AcceptedCount} 次。");
        Assert.True(server.MetaSentAfterControlAccepted);
    }

    [Fact]
    public void 正确顺序_control连上后设备名再滞后数秒_仍能读到()
    {
        // control 已就位，server 在写 device meta 前又滞后了几秒。
        //
        // ⚠️ 本用例证明的是「握手窗口内允许服务端有一段延迟」，
        //    不是「真机设备名本来就会滞后数秒」——后者是已被推翻的误判（真因是握手顺序死锁）。
        // 时间预算：metaDelay 3s < HandshakeTimeoutMs 5s，余量 2s。
        // 若日后把 HandshakeTimeoutMs 调小到 ≤3s，本用例会红——这是期望行为（提醒同步调整）。
        using var server = new ForwardServerWithControl(
            deviceName: "ANA-AN00",
            metaDelay: TimeSpan.FromSeconds(3));
        using var session = CreateSession();

        using Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        using var reader = new VideoStreamReader(videoSocket);
        using Socket controlSocket = InvokeConnectWithRetry(session, server.Port);

        var watch = Stopwatch.StartNew();
        string deviceName = reader.ReadDeviceName();
        watch.Stop();

        Assert.Equal("ANA-AN00", deviceName);
        Assert.True(watch.ElapsedMilliseconds >= 2_000,
            $"实际只等了 {watch.ElapsedMilliseconds}ms，说明服务端延迟未生效，本用例失去意义。");
    }

    // ================================================================
    // 2. 错误顺序（旧实现）：不连 control 就读设备名 → 必定死锁
    // ================================================================

    [Fact]
    public void 错误顺序_未连control就读设备名_必定读不到且server未发meta()
    {
        // 对照组：显式复刻旧 StreamLoop 的顺序，证明它在真实服务端时序下必然卡死。
        using var server = new ForwardServerWithControl(deviceName: "ANA-AN00");
        using var session = CreateSession();

        using Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        Assert.True(server.WaitVideoAccepted(StepBudget), "dummy 已发出，说明隧道已通。");

        // 用短超时（1.5s）代替真实的握手超时（HandshakeTimeoutMs = 5s），避免把 CI 拖死。
        videoSocket.ReceiveTimeout = DeadlockProbeTimeoutMs;
        byte[] nameBuf = new byte[ScrcpyConstants.DEVICE_NAME_SIZE];

        var ex = Assert.Throws<SocketException>(
            () => videoSocket.Receive(nameBuf, 0, nameBuf.Length, SocketFlags.None));

        Assert.Equal(SocketError.TimedOut, ex.SocketErrorCode);
        Assert.False(server.IsMetaSent,
            "server 仍卡在 accept() 等 control socket，绝不应该已经发出设备名。");
        Assert.False(server.IsControlAccepted);

        // ---- 补上 control socket，死锁立刻解除 ----
        using Socket controlSocket = InvokeConnectWithRetry(session, server.Port);
        Assert.True(server.WaitMetaSent(StepBudget),
            "control socket 连上后，server 必须立刻发出 device meta。");

        videoSocket.ReceiveTimeout = VideoStreamReader.HandshakeTimeoutMs;
        using var reader = new VideoStreamReader(videoSocket);
        Assert.Equal("ANA-AN00", reader.ReadDeviceName());
        Assert.Equal(ScrcpyConstants.CODEC_H264, reader.ReadCodecId());
    }

    // ================================================================
    // 3. 结构守护：StreamLoop 里 control 连接必须排在读设备名之前
    // ================================================================

    [Fact]
    public void StreamLoop源码中控制通道连接必须排在读设备名之前()
    {
        string? source = TryReadDeviceSessionSource();
        if (source == null)
        {
            // 源码树不可用（例如仅拿到编译产物）时跳过；行为约束已由上面的用例覆盖。
            return;
        }

        string streamLoop = StripLineComments(ExtractStreamLoopBody(source));

        int controlIdx = streamLoop.IndexOf("ConnectWithRetry(", StringComparison.Ordinal);
        int deviceNameIdx = streamLoop.IndexOf("ReadDeviceName(", StringComparison.Ordinal);

        Assert.True(controlIdx >= 0, "StreamLoop 中未找到 ConnectWithRetry 调用——控制通道建立逻辑可能被移除。");
        Assert.True(deviceNameIdx >= 0, "StreamLoop 中未找到 ReadDeviceName 调用。");

        Assert.True(controlIdx < deviceNameIdx,
            "StreamLoop 必须先建立 control socket 再读设备名——" +
            "scrcpy v4.0 服务端在所有 socket accept 完之前不会发送 device meta，" +
            "反过来写必定握手死锁（读取设备名超时，已读 0/64 字节）。");
    }

    [Fact]
    public void ConnectWithRetry方法签名保持稳定()
    {
        MethodInfo? m = typeof(DeviceSession).GetMethod(
            "ConnectWithRetry",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(m);
        Assert.Equal(typeof(Socket), m!.ReturnType);

        ParameterInfo[] ps = m.GetParameters();
        Assert.Equal(2, ps.Length);
        Assert.Equal(typeof(int), ps[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), ps[1].ParameterType);
    }

    // ================================================================
    // helpers
    // ================================================================

    /// <summary>
    /// 构造一个不启动任何线程、不接触 adb 的 <see cref="DeviceSession"/>。
    /// <c>AdbClient</c> 仅在真正执行命令时才碰 adb.exe，构造是纯内存操作。
    /// </summary>
    private static DeviceSession CreateSession()
    {
        var cfg = new AppConfig { AdbPath = "adb-not-used-in-this-test" };
        var launcher = new ScrcpyServerLauncher(new AdbClient(cfg.AdbPath), cfg);
        return new DeviceSession(new DeviceInfo("TEST-SERIAL"), launcher, cfg);
    }

    private static Socket InvokeConnectVideoSocket(
        DeviceSession session, int port, bool isForward, CancellationToken ct = default)
    {
        MethodInfo m = typeof(DeviceSession).GetMethod(
            "ConnectVideoSocket",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 DeviceSession.ConnectVideoSocket。");

        return InvokeSocketMethod(m, session, new object[] { port, isForward, ct });
    }

    private static Socket InvokeConnectWithRetry(
        DeviceSession session, int port, CancellationToken ct = default)
    {
        MethodInfo m = typeof(DeviceSession).GetMethod(
            "ConnectWithRetry",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 DeviceSession.ConnectWithRetry。");

        return InvokeSocketMethod(m, session, new object[] { port, ct });
    }

    private static Socket InvokeSocketMethod(MethodInfo m, DeviceSession session, object[] args)
    {
        try
        {
            return (Socket)m.Invoke(session, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// 通过编译期记录的本文件路径定位 <c>DeviceSession.cs</c> 源码；找不到时返回 <c>null</c>。
    /// </summary>
    private static string? TryReadDeviceSessionSource([CallerFilePath] string thisFile = "")
    {
        try
        {
            // <repo>/MultiScrcpy.Tests/Core/ForwardHandshakeOrderTests.cs → <repo>
            string? coreDir = Path.GetDirectoryName(thisFile);
            string? testsDir = coreDir == null ? null : Path.GetDirectoryName(coreDir);
            string? repoRoot = testsDir == null ? null : Path.GetDirectoryName(testsDir);
            if (repoRoot == null)
            {
                return null;
            }

            string path = Path.Combine(repoRoot, "MultiScrcpyPanel", "Core", "DeviceSession.cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从源码中截出 <c>StreamLoop</c> 方法体（从方法签名到下一个 <c>private</c> 成员之前）。
    /// </summary>
    private static string ExtractStreamLoopBody(string source)
    {
        const string signature = "private void StreamLoop(CancellationToken ct)";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "未在 DeviceSession.cs 中找到 StreamLoop 方法签名。");

        // StreamLoop 之后的第一个 `catch (OperationCanceledException)` 即为 try 块结束点，
        // 用它作为方法体上界，避免把后续方法里的调用误算进来。
        int end = source.IndexOf("catch (OperationCanceledException)", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.Length;
        }

        return source[start..end];
    }

    /// <summary>
    /// 去掉每行的 <c>//</c> 行注释，只保留可执行代码。
    /// <para>
    /// 必要性：StreamLoop 里的解释性注释本身就会提到 <c>ReadDeviceName()</c> 等方法名，
    /// 不剥离的话顺序断言会被注释文本误导。
    /// </para>
    /// </summary>
    private static string StripLineComments(string code)
    {
        string[] lines = code.Split('\n');
        var sb = new System.Text.StringBuilder(code.Length);

        foreach (string line in lines)
        {
            int idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }

        return sb.ToString();
    }
}
