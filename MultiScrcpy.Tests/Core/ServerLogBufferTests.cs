using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using MultiScrcpy.Core;

using Xunit;

namespace MultiScrcpy.Tests.Core;

/// <summary>
/// <see cref="ServerLogBuffer"/> 回归测试。
/// <para>
/// <b>锁定的行为</b>：设备端 server 日志是握手失败时唯一的「为什么」来源。
/// 若 <see cref="ServerLogBuffer.NormalizeLine"/> 把行清空、
/// 或 <see cref="ServerLogBuffer.LooksLikeError"/> 漏判错误行，
/// 用户就会重新退回到「只看到一句『对端关闭连接』」的老问题。
/// </para>
/// <para>全部为纯函数 / 内存操作，无 adb、无真机、无 socket，可无头执行。</para>
/// </summary>
public class ServerLogBufferTests
{
    // ---------------------------------------------------------------- NormalizeLine

    [Fact]
    public void NormalizeLine_剥掉单层server前缀且内容不为空()
    {
        string actual = ServerLogBuffer.NormalizeLine("[server] INFO: Device: ANA-AN00");

        Assert.Equal("INFO: Device: ANA-AN00", actual);
        Assert.NotEqual(string.Empty, actual);
    }

    [Fact]
    public void NormalizeLine_剥掉重复的多层server前缀()
    {
        // 不同 scrcpy-server 版本会叠加多层前缀；不剥净就会出现
        // "[serial][server:out] [server] [server] INFO: …" 这种噪声。
        string actual = ServerLogBuffer.NormalizeLine("[server] [server]  [server] ERROR: boom");

        Assert.Equal("ERROR: boom", actual);
        Assert.DoesNotContain("[server]", actual, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[SERVER] INFO: mixed case prefix", "INFO: mixed case prefix")]
    [InlineData("   [server]INFO: no space   ", "INFO: no space")]
    [InlineData("  plain line  ", "plain line")]
    [InlineData("INFO: [server] not a prefix", "INFO: [server] not a prefix")]
    public void NormalizeLine_大小写不敏感且只剥前缀不动行内文本(string raw, string expected)
    {
        Assert.Equal(expected, ServerLogBuffer.NormalizeLine(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("[server]")]
    [InlineData("[server] [server]   ")]
    public void NormalizeLine_空白或纯前缀行归一化为空串(string? raw)
    {
        Assert.Equal(string.Empty, ServerLogBuffer.NormalizeLine(raw));
    }

    [Fact]
    public void NormalizeLine_超长行被截断并追加省略号()
    {
        string raw = new('x', ServerLogBuffer.MaxLineLength + 500);

        string actual = ServerLogBuffer.NormalizeLine(raw);

        Assert.Equal(ServerLogBuffer.MaxLineLength + 1, actual.Length);
        Assert.EndsWith("…", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeLine_是纯函数_同输入恒同输出且不改变入参()
    {
        const string raw = "[server] ERROR: Encoder 'c2.android.avc.encoder' not found";

        string first = ServerLogBuffer.NormalizeLine(raw);
        string second = ServerLogBuffer.NormalizeLine(raw);

        Assert.Equal(first, second);
        Assert.Equal("[server] ERROR: Encoder 'c2.android.avc.encoder' not found", raw);
    }

    // ---------------------------------------------------------------- LooksLikeError

    [Theory]
    [InlineData("ERROR: Could not find encoder")]
    [InlineData("java.lang.IllegalStateException: bad state")]
    [InlineData("Exception in thread \"main\"")]
    [InlineData("Permission denied")]
    [InlineData("/system/bin/sh: Permission denied")]
    [InlineData("FATAL EXCEPTION: main")]
    [InlineData("Aborted")]
    [InlineData("ERROR: Video encoding is not supported on this device")]
    public void LooksLikeError_对错误行返回true(string line)
    {
        Assert.True(ServerLogBuffer.LooksLikeError(line),
            $"应被识别为错误行，但返回 false：{line}");
    }

    [Theory]
    [InlineData("INFO: Device: [HUAWEI] ANA-AN00 (Android 12)")]
    [InlineData("INFO: Renderer: opengl")]
    [InlineData("DEBUG: tunnel_forward=true")]
    [InlineData("[server] INFO: starting")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LooksLikeError_对普通行或空行返回false(string? line)
    {
        Assert.False(ServerLogBuffer.LooksLikeError(line),
            $"不应被识别为错误行，但返回 true：{line ?? "<null>"}");
    }

    [Fact]
    public void LooksLikeError_是纯函数_不依赖实例状态()
    {
        // 静态方法必须与任何缓冲实例的状态无关。
        var buffer = new ServerLogBuffer();
        buffer.Append("ERROR: 先污染一下实例状态");

        Assert.True(ServerLogBuffer.LooksLikeError("ERROR: x"));
        Assert.False(ServerLogBuffer.LooksLikeError("INFO: x"));
    }

    // ---------------------------------------------------------------- Append / 环形语义

    [Fact]
    public void Append_返回归一化后的行并计入统计()
    {
        var buffer = new ServerLogBuffer();

        string normalized = buffer.Append("[server] ERROR: boom");

        Assert.Equal("ERROR: boom", normalized);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(1, buffer.TotalCount);
        Assert.Equal(1, buffer.ErrorCount);
        Assert.False(buffer.IsEmpty);
    }

    [Fact]
    public void Append_空行被丢弃且不计入任何统计()
    {
        var buffer = new ServerLogBuffer();

        Assert.Equal(string.Empty, buffer.Append("   "));
        Assert.Equal(string.Empty, buffer.Append(null));
        Assert.Equal(string.Empty, buffer.Append("[server]"));

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.TotalCount);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Append_超过容量时丢弃最旧行但TotalCount继续累加()
    {
        var buffer = new ServerLogBuffer(capacity: 3);

        for (int i = 1; i <= 5; i++)
        {
            buffer.Append($"line{i}");
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(5, buffer.TotalCount);
        Assert.Equal(new[] { "line3", "line4", "line5" }, buffer.Snapshot());
    }

    [Fact]
    public void 构造时容量非正数回退到默认容量()
    {
        Assert.Equal(ServerLogBuffer.DefaultCapacity, new ServerLogBuffer(0).Capacity);
        Assert.Equal(ServerLogBuffer.DefaultCapacity, new ServerLogBuffer(-7).Capacity);
    }

    [Fact]
    public void Snapshot_返回独立副本_后续Append不影响已取快照()
    {
        var buffer = new ServerLogBuffer();
        buffer.Append("a");

        IReadOnlyList<string> snapshot = buffer.Snapshot();
        buffer.Append("b");

        Assert.Single(snapshot);
        Assert.Equal(2, buffer.Count);
    }

    // ---------------------------------------------------------------- Describe

    [Fact]
    public void Describe_空缓冲返回空串()
    {
        Assert.Equal(string.Empty, new ServerLogBuffer().Describe());
    }

    [Fact]
    public void Describe_只输出最近若干行且标注省略行数()
    {
        var buffer = new ServerLogBuffer();
        for (int i = 1; i <= 10; i++)
        {
            buffer.Append($"line{i}");
        }

        string text = buffer.Describe(maxLines: 3);

        Assert.Contains("省略 7 行", text);
        Assert.Contains("line8", text);
        Assert.Contains("line10", text);
        Assert.DoesNotContain("line7", text);
    }

    [Fact]
    public void Describe_行数未超限时不出现省略提示()
    {
        var buffer = new ServerLogBuffer();
        buffer.Append("only");

        string text = buffer.Describe(maxLines: 5);

        Assert.DoesNotContain("省略", text);
        Assert.Contains("only", text);
    }

    // ---------------------------------------------------------------- Clear

    [Fact]
    public void Clear_清空内容与全部计数()
    {
        var buffer = new ServerLogBuffer();
        buffer.Append("ERROR: boom");
        buffer.Append("INFO: ok");

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.TotalCount);
        Assert.Equal(0, buffer.ErrorCount);
        Assert.True(buffer.IsEmpty);
        Assert.Equal(string.Empty, buffer.Describe());
    }

    // ---------------------------------------------------------------- 线程安全

    [Fact]
    public void 并发Append不丢计数不抛异常()
    {
        // 真实场景：stdout / stderr 两个 Process 事件线程同时回调 Append。
        const int writers = 8;
        const int perWriter = 500;
        var buffer = new ServerLogBuffer(capacity: 16);

        Parallel.For(0, writers, w =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                buffer.Append($"[server] w{w}-{i}");
            }
        });

        Assert.Equal(writers * perWriter, buffer.TotalCount);
        Assert.Equal(16, buffer.Count);
        Assert.All(buffer.Snapshot(), line => Assert.DoesNotContain("[server]", line));
    }

    [Fact]
    public async Task 并发读写不抛异常且快照始终自洽()
    {
        var buffer = new ServerLogBuffer(capacity: 10);

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                buffer.Append($"line{i}");
            }
        });

        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                IReadOnlyList<string> snap = buffer.Snapshot();
                Assert.True(snap.Count <= 10);
                _ = buffer.Describe();
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(2000, buffer.TotalCount);
        Assert.Equal(10, buffer.Count);
    }
}
