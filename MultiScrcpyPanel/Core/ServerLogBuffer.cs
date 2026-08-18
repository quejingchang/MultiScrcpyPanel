using System;
using System.Collections.Generic;
using System.Text;

namespace MultiScrcpy.Core;

/// <summary>
/// 设备端 scrcpy-server 输出（stdout + stderr）的定容环形缓冲。
/// <para>
/// <b>存在的意义</b>：握手失败时，设备端的真实原因（编码器不支持 / 权限被拒 / 版本不匹配）
/// 只会出现在 server 自己的输出里。若不把这些行留存下来并回填到异常信息，
/// 用户看到的就只有一句「对端关闭连接」，永远查不出「为什么」。
/// </para>
/// <para>所有成员线程安全：写入来自 <c>Process.OutputDataReceived</c> / <c>ErrorDataReceived</c> 线程池线程，
/// 读取来自会话流线程。</para>
/// </summary>
public sealed class ServerLogBuffer
{
    /// <summary>默认保留的最大行数。</summary>
    public const int DefaultCapacity = 60;

    /// <summary><see cref="Describe"/> 默认输出的最大行数。</summary>
    public const int DefaultDescribeLines = 12;

    /// <summary>单行最大保留字符数，超出截断（防止某些 server 打印超长堆栈把 UI 撑爆）。</summary>
    public const int MaxLineLength = 400;

    private readonly int _capacity;
    private readonly Queue<string> _lines;
    private readonly object _gate = new();

    private int _errorCount;
    private int _totalCount;

    /// <summary>创建缓冲。</summary>
    /// <param name="capacity">保留行数上限；&lt;= 0 时使用 <see cref="DefaultCapacity"/>。</param>
    public ServerLogBuffer(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
        _lines = new Queue<string>(_capacity);
    }

    /// <summary>保留行数上限。</summary>
    public int Capacity => _capacity;

    /// <summary>当前缓冲中的行数（不超过 <see cref="Capacity"/>）。</summary>
    public int Count
    {
        get { lock (_gate) return _lines.Count; }
    }

    /// <summary>历史累计写入的行数（不受容量限制，用于判断 server 是否「一句话都没说」）。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _totalCount; }
    }

    /// <summary>历史累计的疑似错误行数。</summary>
    public int ErrorCount
    {
        get { lock (_gate) return _errorCount; }
    }

    /// <summary>server 是否输出过任何内容。</summary>
    public bool IsEmpty => TotalCount == 0;

    /// <summary>
    /// 追加一行（自动 <see cref="NormalizeLine"/>）；空行被丢弃。
    /// </summary>
    /// <returns>规范化后的行；被丢弃时返回空串。</returns>
    public string Append(string? rawLine)
    {
        string line = NormalizeLine(rawLine);
        if (line.Length == 0)
        {
            return string.Empty;
        }

        bool isError = LooksLikeError(line);

        lock (_gate)
        {
            while (_lines.Count >= _capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
            _totalCount++;
            if (isError)
            {
                _errorCount++;
            }
        }

        return line;
    }

    /// <summary>取当前缓冲的快照（按写入先后顺序）。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }

    /// <summary>
    /// 拼出「最近若干行」的多行文本，供异常信息 / UI 展示；缓冲为空时返回空串。
    /// </summary>
    /// <param name="maxLines">最多输出的行数；&lt;= 0 时使用 <see cref="DefaultDescribeLines"/>。</param>
    public string Describe(int maxLines = DefaultDescribeLines)
    {
        int limit = maxLines > 0 ? maxLines : DefaultDescribeLines;

        string[] all;
        lock (_gate)
        {
            all = _lines.ToArray();
        }

        if (all.Length == 0)
        {
            return string.Empty;
        }

        int start = all.Length > limit ? all.Length - limit : 0;
        var sb = new StringBuilder();

        if (start > 0)
        {
            sb.Append("  …（省略 ").Append(start).Append(" 行）").Append(Environment.NewLine);
        }

        for (int i = start; i < all.Length; i++)
        {
            sb.Append("  ").Append(all[i]);
            if (i < all.Length - 1)
            {
                sb.Append(Environment.NewLine);
            }
        }

        return sb.ToString();
    }

    /// <summary>清空缓冲（计数一并归零）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _errorCount = 0;
            _totalCount = 0;
        }
    }

    /// <summary>
    /// 规范化 server 输出的一行（纯函数，可无头单测）：
    /// <list type="number">
    ///   <item><description>裁掉首尾空白与结尾的 <c>\r</c>；</description></item>
    ///   <item><description>剥掉 server 自带的 <c>[server]</c> 前缀——我们打日志时会再加一层
    ///   <c>[serial][server:out]</c>，不剥就会出现
    ///   <c>[serial][server] [server] INFO: …</c> 这样的重复前缀；</description></item>
    ///   <item><description>超长行截断到 <see cref="MaxLineLength"/> 并追加省略号。</description></item>
    /// </list>
    /// </summary>
    public static string NormalizeLine(string? rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return string.Empty;
        }

        string line = rawLine.Trim();

        // server 可能连续输出多层 "[server]" 前缀（不同版本行为不一致），循环剥净。
        while (line.StartsWith("[server]", StringComparison.OrdinalIgnoreCase))
        {
            line = line.Substring("[server]".Length).TrimStart();
        }

        if (line.Length == 0)
        {
            return string.Empty;
        }

        if (line.Length > MaxLineLength)
        {
            line = line.Substring(0, MaxLineLength) + "…";
        }

        return line;
    }

    /// <summary>
    /// 判断一行 server 输出是否像错误（纯函数，可无头单测）。
    /// <para>覆盖 scrcpy-server 的 <c>ERROR:</c> 前缀、Java 异常、Android 崩溃关键字。</para>
    /// </summary>
    public static bool LooksLikeError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string text = line;

        return Contains(text, "ERROR")
               || Contains(text, "FATAL")
               || Contains(text, "Exception")
               || Contains(text, "java.lang.")
               || Contains(text, "Aborted")
               || Contains(text, "not supported")
               || Contains(text, "Permission denied");
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
