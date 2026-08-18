using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace MultiScrcpy.Core.Adb;

/// <summary>
/// 本地端口分配器（架构文档 §8 T02-2）：从基准端口起递增，
/// 用 <see cref="TcpListener"/> bind 探测可用性，并维护已用集合，线程安全。
/// </summary>
public sealed class PortAllocator
{
    private const int MaxProbe = 200;

    private readonly int _base;
    private readonly HashSet<int> _used = new();
    private readonly object _lock = new();

    public PortAllocator(int basePort = 27183)
    {
        _base = basePort is > 1024 and < 60000 ? basePort : 27183;
    }

    /// <summary>当前已占用端口数量。</summary>
    public int UsedCount
    {
        get { lock (_lock) return _used.Count; }
    }

    /// <summary>
    /// 申请一个可用端口。
    /// </summary>
    /// <exception cref="ServerLaunchException">连续探测 <c>MaxProbe</c> 个端口都不可用。</exception>
    public int Acquire()
    {
        lock (_lock)
        {
            for (int offset = 0; offset < MaxProbe; offset++)
            {
                int port = _base + offset;
                if (port > 65535) break;
                if (_used.Contains(port)) continue;
                if (!IsFree(port)) continue;

                _used.Add(port);
                return port;
            }
        }

        throw new ServerLaunchException($"无可用本地端口（已从 {_base} 起连续探测 {MaxProbe} 个）。");
    }

    /// <summary>归还端口（幂等）。</summary>
    public void Release(int port)
    {
        lock (_lock)
        {
            _used.Remove(port);
        }
    }

    /// <summary>bind 探测端口是否空闲。</summary>
    private static bool IsFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* 忽略 */ }
        }
    }
}
