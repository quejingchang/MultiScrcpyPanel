using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

using MultiScrcpy.Core;
using MultiScrcpy.Core.Adb;
using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// 对 <c>DeviceSession.StreamLoop</c> 握手顺序的<b>编译产物级</b>回归守护。
///
/// <para>
/// <b>为什么需要本文件（QA 变异实验结论）</b>：把 <c>StreamLoop</c> 改回死锁顺序
/// （先 <c>ReadDeviceName()</c> 再连 control socket）后，实测
/// <see cref="ForwardHandshakeOrderTests"/> 与 <see cref="HandshakeTimeoutTests"/> 共 24 条用例中
/// <b>只有 1 条</b>变红——即基于<b>源码文本</b>扫描的
/// <c>StreamLoop源码中控制通道连接必须排在读设备名之前</c>。
/// 其余「行为型」用例全部保持绿色，因为它们并不驱动 <c>StreamLoop</c> 本身，
/// 而是在测试方法体内用反射手工按正确顺序调用
/// <c>ConnectVideoSocket</c> / <c>ConnectWithRetry</c>——顺序是<b>测试自己写的</b>，
/// 生产代码怎么改都影响不到它们。
/// </para>
///
/// <para>
/// 而那唯一的源码文本守护有两个弱点：
/// <list type="number">
///   <item>依赖源码树存在（<c>[CallerFilePath]</c>），找不到文件时<b>静默 return 判过</b>；
///         在仅有编译产物的环境（打包 / 跨机重放 / 源码目录被移动）会退化成「永远绿」。</item>
///   <item>基于文本 <c>IndexOf</c>，对格式化、字符串字面量、块注释等敏感。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>本文件的做法</b>：直接解析 <c>StreamLoop</c> 的 <b>IL 字节码</b>，按真实执行顺序取出
/// 全部方法调用，断言
/// <c>DeviceSession.ConnectWithRetry</c> 与 <c>DeviceController.Start</c>
/// 都排在 <c>VideoStreamReader.ReadDeviceName</c> <b>之前</b>。
/// 它作用于编译产物，<b>不依赖源码树、不可能静默跳过</b>，且对注释/格式化完全免疫。
/// </para>
/// </summary>
public class StreamLoopHandshakeOrderIlTests
{
    // ================================================================
    // 1. 核心：IL 层面的调用顺序守护
    // ================================================================

    [Fact]
    public void StreamLoop的IL中控制通道连接必须排在读设备名之前()
    {
        IReadOnlyList<string> calls = ExtractStreamLoopCalls();

        int controlIdx = IndexOfCall(calls, "DeviceSession.ConnectWithRetry");
        int deviceNameIdx = IndexOfCall(calls, "VideoStreamReader.ReadDeviceName");

        Assert.True(controlIdx >= 0,
            $"StreamLoop 的 IL 中未找到 DeviceSession.ConnectWithRetry 调用——" +
            $"控制通道建立逻辑可能被移除。实际调用序列：{Describe(calls)}");

        Assert.True(deviceNameIdx >= 0,
            $"StreamLoop 的 IL 中未找到 VideoStreamReader.ReadDeviceName 调用。" +
            $"实际调用序列：{Describe(calls)}");

        Assert.True(controlIdx < deviceNameIdx,
            "StreamLoop 必须先建立 control socket 再读设备名。" +
            "scrcpy v4.0 的 DesktopConnection 用同一个 LocalServerSocket 顺序 accept " +
            "(video → audio → control)，全部 accept 完之后才 sendDeviceMeta()；" +
            "反过来写会导致客户端阻塞在 ReadDeviceName()、服务端阻塞在 accept(control) —— 握手死锁" +
            "（真机现象：读取设备名超时（已读 0/64 字节））。" +
            $"实际调用序列：{Describe(calls)}");
    }

    [Fact]
    public void StreamLoop的IL中控制通道必须在读设备名之前就已启动()
    {
        // 只连 socket 不 Start 也能解开 server 的 accept 阻塞，但控制通道必须同步就绪，
        // 否则「设备名读到了、控制指令却发不出去」会变成另一类难查的偶发故障。
        IReadOnlyList<string> calls = ExtractStreamLoopCalls();

        int startIdx = IndexOfCall(calls, "DeviceController.Start");
        int deviceNameIdx = IndexOfCall(calls, "VideoStreamReader.ReadDeviceName");

        Assert.True(startIdx >= 0,
            $"StreamLoop 的 IL 中未找到 DeviceController.Start 调用。实际调用序列：{Describe(calls)}");

        Assert.True(startIdx < deviceNameIdx,
            "DeviceController.Start() 必须排在 ReadDeviceName() 之前，与 control socket 的建立保持同一阶段。" +
            $"实际调用序列：{Describe(calls)}");
    }

    [Fact]
    public void StreamLoop的IL中读设备名必须排在读codecid之前()
    {
        // 线格式顺序：设备名 64 字节 → codec id u32 大端。颠倒会整体错位。
        IReadOnlyList<string> calls = ExtractStreamLoopCalls();

        int nameIdx = IndexOfCall(calls, "VideoStreamReader.ReadDeviceName");
        int codecIdx = IndexOfCall(calls, "VideoStreamReader.ReadCodecId");

        Assert.True(codecIdx >= 0,
            $"StreamLoop 的 IL 中未找到 VideoStreamReader.ReadCodecId 调用。实际调用序列：{Describe(calls)}");

        Assert.True(nameIdx < codecIdx,
            $"必须先读 64 字节设备名再读 u32 codec id。实际调用序列：{Describe(calls)}");
    }

    [Fact]
    public void StreamLoop的IL中不得在读设备名前后重复消费dummy字节()
    {
        // dummy 已在 ConnectVideoSocket 的重试单元内被 TryReadDummyByte 吃掉。
        // 若 StreamLoop 再调 ReadHandshake(isForward:true)，会多吃 1 字节，设备名整体错位。
        IReadOnlyList<string> calls = ExtractStreamLoopCalls();

        Assert.DoesNotContain(calls, c => c == "VideoStreamReader.ReadHandshake");
        Assert.DoesNotContain(calls, c => c == "VideoStreamReader.TryReadDummyByte");

        // 设备名只应被读一次。
        Assert.Equal(1, calls.Count(c => c == "VideoStreamReader.ReadDeviceName"));
    }

    /// <summary>
    /// IL walker 的自检：如果 walker 本身写错（例如操作数长度算错导致乱解码），
    /// 上面几条顺序断言可能因为「什么都没找到」而误判。这里先钉死一组必然存在的调用。
    /// </summary>
    [Fact]
    public void IL解析器自检_StreamLoop中必然存在的调用都应被解析到()
    {
        IReadOnlyList<string> calls = ExtractStreamLoopCalls();

        string[] mustExist =
        {
            "DeviceSession.ConnectVideoSocket",
            "DeviceSession.ConnectWithRetry",
            "VideoStreamReader.ReadDeviceName",
            "VideoStreamReader.ReadCodecId",
            "VideoStreamReader.EnterStreamingMode",
            "DeviceSession.PumpPackets",
        };

        foreach (string expected in mustExist)
        {
            Assert.True(
                calls.Contains(expected),
                $"IL 解析器未能解析出必然存在的调用 {expected}——解析器可能已失效，" +
                $"顺序断言将失去意义。实际调用序列：{Describe(calls)}");
        }
    }

    // ================================================================
    // 2. 边界与错误路径：control socket 连接失败 / 被取消
    // ================================================================

    [Fact]
    public void ConnectWithRetry_端口无人监听时在预算内抛ProtocolException()
    {
        using var session = CreateSession();
        int deadPort = ReserveDeadPort();

        // ⭐ 自标定：不同机器上「connect 到无人监听端口」的耗时差异极大
        // （本项目开发机因过滤驱动约 2000ms，纯净机通常 < 1ms）。
        // 而 ConnectWithRetry 用的是**阻塞** Socket.Connect，单次 connect 的耗时会直接叠加进总耗时，
        // 所以这里先实测单次成本，再据此推导上界，避免把机器特性写死成断言。
        long singleConnectMs = MeasureSingleFailedConnectMs(deadPort);
        int maxAttempts = RetryDelayCount() + 1;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<ProtocolException>(
            () => InvokeConnectWithRetry(session, deadPort, CancellationToken.None));
        watch.Stop();

        Assert.Contains($"{deadPort}", ex.Message);

        // 上界 = 退避总预算 + 最多 maxAttempts 次阻塞 connect 的成本 + 1s 余量。
        long upperBound = DeviceSession.ConnectBudgetMs
                          + (maxAttempts * Math.Max(singleConnectMs, 1))
                          + 1_000;

        Assert.True(
            watch.ElapsedMilliseconds < upperBound,
            $"control socket 连接失败耗时 {watch.ElapsedMilliseconds}ms，超过上界 {upperBound}ms" +
            $"（退避预算 {DeviceSession.ConnectBudgetMs}ms + 最多 {maxAttempts} 次 × " +
            $"单次 connect {singleConnectMs}ms + 1000ms 余量）——退避/预算封顶逻辑可能已失效。");
    }

    [Fact]
    public void ConnectWithRetry_取消令牌已取消时立即抛OperationCanceled()
    {
        using var session = CreateSession();
        int deadPort = ReserveDeadPort();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => InvokeConnectWithRetry(session, deadPort, cts.Token));
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 500,
            $"已取消的令牌应立即退出，实际耗时 {watch.ElapsedMilliseconds}ms。");
    }

    [Fact]
    public void ConnectWithRetry_重试途中被取消应显著快于跑满整个重试预算()
    {
        // 自标定对照实验：同一台机器上先测「不取消」的完整耗时，再测「早取消」的耗时，
        // 断言后者显著更短。这样不依赖任何机器相关的绝对毫秒数。
        using var session = CreateSession();
        int deadPort = ReserveDeadPort();

        // 基线：跑满退避预算。
        var fullWatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ProtocolException>(
            () => InvokeConnectWithRetry(session, deadPort, CancellationToken.None));
        fullWatch.Stop();

        // 对照：150ms 后取消。
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        var cancelWatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(
            () => InvokeConnectWithRetry(session, deadPort, cts.Token));
        cancelWatch.Stop();

        Assert.True(
            cancelWatch.ElapsedMilliseconds < fullWatch.ElapsedMilliseconds,
            $"被取消耗时 {cancelWatch.ElapsedMilliseconds}ms，未短于跑满预算的 " +
            $"{fullWatch.ElapsedMilliseconds}ms——退避等待未响应取消令牌。");

        // ⚠️ 已知限制：ConnectWithRetry 用的是**阻塞** Socket.Connect，它不接受 CancellationToken。
        // 因此取消只能在两次 connect 之间的退避等待里被观察到，最坏情况要多等「一次 connect」的时间。
        // 这里把这条契约显式钉住：取消后的耗时不应超过「一次 connect + 一个最大退避间隔」的量级。
        long singleConnectMs = MeasureSingleFailedConnectMs(deadPort);
        long tolerated = singleConnectMs + MaxRetryDelayMs() + 1_000;

        Assert.True(
            cancelWatch.ElapsedMilliseconds < tolerated,
            $"取消后耗时 {cancelWatch.ElapsedMilliseconds}ms，超过「一次阻塞 connect " +
            $"({singleConnectMs}ms) + 最大退避间隔 ({MaxRetryDelayMs()}ms) + 1000ms 余量」= {tolerated}ms。");
    }

    [Fact]
    public void 控制通道连接失败后停止会话_已连的videoSocket必须被关闭()
    {
        // 复刻 StreamLoop 的失败路径：video 已连上（dummy 已消费），control 连接失败抛异常，
        // 外层 catch 走 ReleaseServerResources()。此时绝不能泄漏 video socket，
        // 否则设备端 server 会以为客户端还在，残留进程污染下一次连接。
        using var server = new ForwardServerWithControl(deviceName: "ANA-AN00");
        using var session = CreateSession();

        Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        Assert.True(videoSocket.Connected);
        Assert.True(server.WaitVideoAccepted(TimeSpan.FromSeconds(10)));

        // StreamLoop 在 ConnectVideoSocket 之后立刻构造 _reader，这里等价地注入。
        SetPrivateField(session, "_reader", new VideoStreamReader(videoSocket));

        // control 连接失败。
        int deadPort = ReserveDeadPort();
        Assert.Throws<ProtocolException>(
            () => InvokeConnectWithRetry(session, deadPort, CancellationToken.None));

        // 失败路径的清理（Stop 与异常分支共用同一套幂等清理）。
        session.Stop();

        Assert.False(videoSocket.Connected,
            "control socket 连接失败后，已建立的 video socket 必须被关闭，否则会泄漏并残留设备端 server。");
    }

    [Fact]
    public void 会话清理是幂等的_重复Stop与Dispose不抛异常()
    {
        using var server = new ForwardServerWithControl(deviceName: "ANA-AN00");
        var session = CreateSession();

        Socket videoSocket = InvokeConnectVideoSocket(session, server.Port, isForward: true);
        SetPrivateField(session, "_reader", new VideoStreamReader(videoSocket));

        session.Stop();
        session.Stop();
        session.Dispose();
        session.Dispose();

        Assert.False(videoSocket.Connected);
    }

    // ================================================================
    // 3. 夹具自检：ForwardServerWithControl 必须真的扣着 device meta 不发
    // ================================================================

    [Fact]
    public void 夹具自检_第二个连接到达前绝不发送device_meta()
    {
        // 顺序类用例全部建立在这个夹具行为之上。若夹具退化成「accept 完 video 就发 meta」，
        // 所有顺序断言都会变成空转。这里独立钉死夹具契约本身。
        using var server = new ForwardServerWithControl(deviceName: "ANA-AN00");

        using Socket video = Connect(server.Port);
        Assert.True(server.WaitVideoAccepted(TimeSpan.FromSeconds(10)));

        // dummy 必须已经到达（证明 server 确实在阶段 1）。
        Assert.True(
            VideoStreamReader.TryReadDummyByte(video, 2_000, out string reason),
            $"夹具应在 accept video 之后立即写出 dummy，实际失败：{reason}");

        // 给夹具充分的时间「犯错」；它必须什么都不发。
        Assert.False(server.WaitMetaSent(TimeSpan.FromMilliseconds(800)),
            "control socket 尚未连接，夹具绝不应发送 device meta——夹具已失去顺序约束力。");
        Assert.False(server.IsMetaSent);
        Assert.False(server.IsControlAccepted);

        // 第二个连接到达后，meta 必须立刻出现。
        using Socket control = Connect(server.Port);
        Assert.True(server.WaitMetaSent(TimeSpan.FromSeconds(10)),
            "第二个连接 accept 之后，夹具必须立刻发送 device meta。");
        Assert.True(server.MetaSentAfterControlAccepted);
    }

    // ================================================================
    // IL walker
    // ================================================================

    private static IReadOnlyList<string> ExtractStreamLoopCalls()
    {
        MethodInfo streamLoop = typeof(DeviceSession).GetMethod(
            "StreamLoop",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 DeviceSession.StreamLoop。");

        MethodBody body = streamLoop.GetMethodBody()
            ?? throw new InvalidOperationException("DeviceSession.StreamLoop 没有方法体。");

        byte[] il = body.GetILAsByteArray()
            ?? throw new InvalidOperationException("无法读取 StreamLoop 的 IL。");

        Module module = streamLoop.Module;
        Type[] typeArgs = typeof(DeviceSession).GetGenericArguments();
        Type[] methodArgs = streamLoop.GetGenericArguments();

        var calls = new List<string>();
        int pos = 0;

        while (pos < il.Length)
        {
            OpCode op = ReadOpCode(il, ref pos);

            if (op.OperandType is OperandType.InlineMethod)
            {
                int token = BitConverter.ToInt32(il, pos);
                string? name = TryResolveMethodName(module, token, typeArgs, methodArgs);
                if (name != null)
                {
                    calls.Add(name);
                }
            }

            pos += OperandSize(op, il, pos);
        }

        return calls;
    }

    private static OpCode ReadOpCode(byte[] il, ref int pos)
    {
        byte first = il[pos];
        if (first == 0xFE)
        {
            OpCode two = TwoByteOpCodes[il[pos + 1]];
            pos += 2;
            return two;
        }

        OpCode one = OneByteOpCodes[first];
        pos += 1;
        return one;
    }

    private static int OperandSize(OpCode op, byte[] il, int operandPos) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
        OperandType.ShortInlineI or
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
        OperandType.InlineField or
        OperandType.InlineI or
        OperandType.InlineMethod or
        OperandType.InlineSig or
        OperandType.InlineString or
        OperandType.InlineTok or
        OperandType.InlineType or
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or
        OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, operandPos) * 4),
        _ => throw new NotSupportedException($"未处理的 IL 操作数类型：{op.OperandType}"),
    };

    private static string? TryResolveMethodName(
        Module module, int token, Type[] typeArgs, Type[] methodArgs)
    {
        try
        {
            MethodBase? m = module.ResolveMethod(token, typeArgs, methodArgs);
            if (m?.DeclaringType == null)
            {
                return null;
            }

            return $"{m.DeclaringType.Name}.{m.Name}";
        }
        catch (ArgumentException)
        {
            // 泛型实例化 / 非方法 token，与握手顺序无关，忽略。
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static readonly OpCode[] OneByteOpCodes = BuildOneByteOpCodes();
    private static readonly OpCode[] TwoByteOpCodes = BuildTwoByteOpCodes();

    private static OpCode[] BuildOneByteOpCodes()
    {
        var table = new OpCode[0x100];
        foreach (OpCode op in AllOpCodes())
        {
            ushort value = unchecked((ushort)op.Value);
            if (value < 0x100)
            {
                table[value] = op;
            }
        }

        return table;
    }

    private static OpCode[] BuildTwoByteOpCodes()
    {
        var table = new OpCode[0x100];
        foreach (OpCode op in AllOpCodes())
        {
            ushort value = unchecked((ushort)op.Value);
            if ((value & 0xFF00) == 0xFE00)
            {
                table[value & 0xFF] = op;
            }
        }

        return table;
    }

    private static IEnumerable<OpCode> AllOpCodes()
    {
        return typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!);
    }

    private static int IndexOfCall(IReadOnlyList<string> calls, string name)
    {
        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(IReadOnlyList<string> calls) => string.Join(" → ", calls);

    // ================================================================
    // helpers
    // ================================================================

    private static DeviceSession CreateSession()
    {
        var cfg = new AppConfig { AdbPath = "adb-not-used-in-this-test" };
        var launcher = new ScrcpyServerLauncher(new AdbClient(cfg.AdbPath), cfg);
        return new DeviceSession(new DeviceInfo("TEST-SERIAL"), launcher, cfg);
    }

    private static void SetPrivateField(object target, string field, object value)
    {
        FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"未找到私有字段 {field}。");
        f.SetValue(target, value);
    }

    private static Socket InvokeConnectVideoSocket(
        DeviceSession session, int port, bool isForward, CancellationToken ct = default)
    {
        MethodInfo m = typeof(DeviceSession).GetMethod(
            "ConnectVideoSocket", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("未找到 DeviceSession.ConnectVideoSocket。");

        return InvokeSocketMethod(m, session, new object[] { port, isForward, ct });
    }

    private static Socket InvokeConnectWithRetry(
        DeviceSession session, int port, CancellationToken ct)
    {
        MethodInfo m = typeof(DeviceSession).GetMethod(
            "ConnectWithRetry", BindingFlags.Instance | BindingFlags.NonPublic)
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
    /// 实测本机「阻塞 connect 到无人监听端口」的单次耗时（毫秒）。
    /// <para>
    /// 用于把机器相关的时间成本从断言里剥离出去：纯净机通常 &lt; 1ms，
    /// 装了网络过滤驱动 / 安全软件的机器可能到 2000ms 量级。
    /// </para>
    /// </summary>
    private static long MeasureSingleFailedConnectMs(int deadPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            socket.Connect(IPAddress.Loopback, deadPort);
        }
        catch (SocketException)
        {
            // 预期路径。
        }
        finally
        {
            watch.Stop();
            socket.Dispose();
        }

        return watch.ElapsedMilliseconds;
    }

    /// <summary>读取 <c>DeviceSession.RetryDelaysMs</c> 的长度，避免在测试里硬编码重试次数。</summary>
    private static int RetryDelayCount() => RetryDelays().Length;

    /// <summary>读取 <c>DeviceSession.RetryDelaysMs</c> 的最大退避间隔。</summary>
    private static int MaxRetryDelayMs() => RetryDelays().Max();

    private static int[] RetryDelays()
    {
        FieldInfo f = typeof(DeviceSession).GetField(
            "RetryDelaysMs", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("未找到 DeviceSession.RetryDelaysMs。");

        return (int[])f.GetValue(null)!;
    }

    /// <summary>占一个端口再立刻释放，得到一个「本机确定无人监听」的端口号。</summary>
    private static int ReserveDeadPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        socket.Connect(IPAddress.Loopback, port);
        return socket;
    }
}
