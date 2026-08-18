using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MultiScrcpy.Core.Adb;

namespace MultiScrcpy.Core;

/// <summary>
/// 多设备编排中枢（架构文档 §8-T04-3）。
/// <para>
/// 不持有任何计时器（Timer 由 <c>MainForm</c> 驱动，避免 Core 依赖 WinForms），
/// 对外暴露 <see cref="ScanOnce"/> / <see cref="PollStatus"/> 供 UI 定时调用。
/// </para>
/// <para>
/// <b>⚠️ 线程模型</b>：所有事件均可能在<b>后台线程</b>触发，UI 订阅者必须 <c>SafePost</c>。
/// </para>
/// </summary>
public sealed class DeviceManager : IDisposable
{
    /// <summary><see cref="ShutdownAll"/> 的总超时（毫秒）。</summary>
    public const int ShutdownTimeoutMs = 10_000;

    private readonly AppConfig _cfg;
    private readonly AdbClient _adb;
    private readonly ScrcpyServerLauncher _launcher;

    private readonly Dictionary<string, DeviceSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceInfo> _known = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private int _scanning;
    private int _polling;
    private bool _disposed;

    /// <summary>创建管理器。</summary>
    /// <param name="cfg">全局配置。</param>
    /// <param name="adb">ADB 客户端。</param>
    public DeviceManager(AppConfig cfg, AdbClient adb)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _launcher = new ScrcpyServerLauncher(_adb, _cfg);
    }

    /// <summary>发现新设备（参数：设备信息）。</summary>
    public event Action<DeviceInfo>? DeviceAdded;

    /// <summary>设备移除（参数：serial）。</summary>
    public event Action<string>? DeviceRemoved;

    /// <summary>设备状态 / 型号 / 电量更新（参数：设备信息）。</summary>
    public event Action<DeviceInfo>? DeviceStatusUpdated;

    /// <summary>错误通知（参数：serial（可能为空串表示全局）, message）。</summary>
    public event Action<string, string>? ErrorOccurred;

    /// <summary>一轮扫描结束（无论结果）；参数：当前已知设备数、本轮是否发生错误。</summary>
    public event Action<int, bool>? ScanCompleted;

    /// <summary>ADB 客户端（供 UI 展示路径等只读用途）。</summary>
    public AdbClient Adb => _adb;

    /// <summary>全局配置。</summary>
    public AppConfig Config => _cfg;

    /// <summary>当前活跃会话数。</summary>
    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>取一份当前已知设备信息快照（浅拷贝列表，元素为共享实例）。</summary>
    public IReadOnlyList<DeviceInfo> Snapshot()
    {
        lock (_gate)
        {
            return _known.Values.ToList();
        }
    }

    /// <summary>按序列号取会话；不存在返回 <c>null</c>。</summary>
    public DeviceSession? GetSession(string serial)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(serial, out DeviceSession? s) ? s : null;
        }
    }

    /// <summary>
    /// 扫描一次设备列表并 diff 出新增 / 移除 / 状态变化。
    /// 上一次扫描尚未完成时直接跳过（<c>Interlocked</c> 守卫）。
    /// </summary>
    public void ScanOnce()
    {
        if (_disposed || Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            return;
        }

        if (!_adb.IsAvailable)
        {
            // adb 未配置：避免每 2 秒重复抛异常刷屏；首次提示已由 MainForm 启动错误（Toast + 状态栏）承担。
            Interlocked.Exchange(ref _scanning, 0);
            return;
        }

        bool hadError = false;

        try
        {
            IReadOnlyList<DeviceInfo> current;
            try
            {
                current = _adb.Devices();
            }
            catch (Exception ex)
            {
                Log.Warn($"设备扫描失败：{ex.Message}");
                hadError = true;
                ErrorOccurred?.Invoke(string.Empty, $"ADB 扫描失败：{ex.Message}");
                return;
            }

            var currentMap = new Dictionary<string, DeviceInfo>(StringComparer.Ordinal);
            foreach (DeviceInfo d in current)
            {
                currentMap[d.Serial] = d;
            }

            var added = new List<DeviceInfo>();
            var updated = new List<DeviceInfo>();
            var removed = new List<string>();

            lock (_gate)
            {
                foreach (KeyValuePair<string, DeviceInfo> pair in currentMap)
                {
                    if (_known.TryGetValue(pair.Key, out DeviceInfo? old))
                    {
                        bool changed = old.State != pair.Value.State
                                       || (pair.Value.Model.Length > 0 && old.Model != pair.Value.Model);
                        old.MergeFrom(pair.Value);
                        if (changed)
                        {
                            updated.Add(old);
                        }
                    }
                    else
                    {
                        _known[pair.Key] = pair.Value;
                        added.Add(pair.Value);
                    }
                }

                foreach (string serial in _known.Keys.ToList())
                {
                    if (!currentMap.ContainsKey(serial))
                    {
                        _known.Remove(serial);
                        removed.Add(serial);
                    }
                }
            }

            foreach (string serial in removed)
            {
                Log.Info($"设备已移除：{serial}");
                Detach(serial);
                DeviceRemoved?.Invoke(serial);
            }

            foreach (DeviceInfo info in added)
            {
                Log.Info($"发现设备：{info.Serial}（{info.State}，{info.Model}）");
                DeviceAdded?.Invoke(info);
            }

            foreach (DeviceInfo info in updated)
            {
                DeviceStatusUpdated?.Invoke(info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("ScanOnce 未预期异常。", ex);
            hadError = true;
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);

            // 本轮已结束：把「已知设备数 + 是否出错」告知 UI，供其把状态栏从「正在扫描…」改写为终态。
            // 计数在锁内读取（与 Snapshot/SessionCount 的加锁纪律一致），事件在锁外触发以避免订阅者回调时死锁。
            int knownCount;
            lock (_gate)
            {
                knownCount = _known.Count;
            }

            ScanCompleted?.Invoke(knownCount, hadError);
        }
    }

    /// <summary>
    /// 为设备创建并启动会话。
    /// <list type="bullet">
    ///   <item>已达 <see cref="AppConfig.MaxDevices"/> 上限 → 触发 <see cref="ErrorOccurred"/> 并返回 <c>null</c>（不建线程、不建会话）；</item>
    ///   <item><see cref="DeviceState.Unauthorized"/> → 只建卡片，直接返回 <c>null</c>（不 push jar、不起 server）；</item>
    ///   <item>已存在会话 → 直接返回原会话。</item>
    /// </list>
    /// </summary>
    public DeviceSession? Attach(string serial)
    {
        if (_disposed || string.IsNullOrEmpty(serial))
        {
            return null;
        }

        DeviceInfo? info;
        lock (_gate)
        {
            if (_sessions.TryGetValue(serial, out DeviceSession? existing))
            {
                return existing;
            }

            if (_sessions.Count >= _cfg.MaxDevices)
            {
                string msg = $"已达设备上限 {_cfg.MaxDevices} 台，未接入该设备";
                Log.Warn($"[{serial}] {msg}");
                ErrorOccurred?.Invoke(serial, msg);
                return null;
            }

            if (!_known.TryGetValue(serial, out info))
            {
                info = new DeviceInfo(serial, DeviceState.Detected);
                _known[serial] = info;
            }

            if (info.State == DeviceState.Unauthorized)
            {
                Log.Info($"[{serial}] 设备待授权，仅创建卡片，不启动会话。");
                return null;
            }

            if (info.State == DeviceState.Offline)
            {
                Log.Info($"[{serial}] 设备离线，不启动会话。");
                return null;
            }
        }

        var session = new DeviceSession(info, _launcher, _cfg);
        session.StateChanged += OnSessionStateChanged;
        session.ErrorOccurred += OnSessionError;

        lock (_gate)
        {
            if (_sessions.ContainsKey(serial))
            {
                session.Dispose();
                return _sessions[serial];
            }

            _sessions[serial] = session;
        }

        session.Start();
        Log.Info($"[{serial}] 会话已创建（当前 {SessionCount}/{_cfg.MaxDevices} 台）。");
        return session;
    }

    /// <summary>停止并移除会话；不存在时静默返回。<b>幂等。</b></summary>
    public void Detach(string serial)
    {
        DeviceSession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(serial, out session))
            {
                return;
            }

            _sessions.Remove(serial);
        }

        session.StateChanged -= OnSessionStateChanged;
        session.ErrorOccurred -= OnSessionError;

        try
        {
            session.Stop();
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] 停止会话异常：{ex.Message}");
        }

        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] 释放会话异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 重新授权（PRD Q7）：后台执行 <c>adb reconnect</c> → 重扫 → 若已授权则自动接入。
    /// <b>全程不阻塞调用线程，异常只记 WARN。</b>
    /// </summary>
    public void RetryAuthorize(string serial)
    {
        if (_disposed || string.IsNullOrEmpty(serial))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _adb.Reconnect(serial);
            }
            catch (Exception ex)
            {
                Log.Warn($"[{serial}] adb reconnect 失败：{ex.Message}");
            }

            try
            {
                ScanOnce();
            }
            catch (Exception ex)
            {
                Log.Warn($"[{serial}] 重新授权后扫描失败：{ex.Message}");
            }

            DeviceInfo? info;
            lock (_gate)
            {
                _known.TryGetValue(serial, out info);
            }

            if (info == null)
            {
                Log.Warn($"[{serial}] 重新授权后未在设备列表中找到该设备。");
                return;
            }

            if (info.State == DeviceState.Detected)
            {
                Attach(serial);
            }

            DeviceStatusUpdated?.Invoke(info);
        });
    }

    /// <summary>后台轮询在线设备电量（型号已在 <c>Devices()</c> 中取得）。</summary>
    public void PollStatus()
    {
        if (_disposed || Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                List<DeviceInfo> targets;
                lock (_gate)
                {
                    targets = _known.Values.Where(d => d.State != DeviceState.Unauthorized
                                                       && d.State != DeviceState.Offline).ToList();
                }

                foreach (DeviceInfo info in targets)
                {
                    int battery = _adb.GetBattery(info.Serial);
                    if (battery >= 0 && battery != info.Battery)
                    {
                        info.Battery = battery;
                        DeviceStatusUpdated?.Invoke(info);
                    }

                    if (string.IsNullOrEmpty(info.Model))
                    {
                        string model = _adb.GetModel(info.Serial);
                        if (model.Length > 0)
                        {
                            info.Model = model;
                            DeviceStatusUpdated?.Invoke(info);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"状态轮询异常：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        });
    }

    /// <summary>
    /// 并行关闭全部会话，总超时 <see cref="ShutdownTimeoutMs"/>；供 <c>FormClosing</c> 调用。
    /// 结束后确保没有残留 <c>adb forward</c>。
    /// </summary>
    public void ShutdownAll()
    {
        List<string> serials;
        lock (_gate)
        {
            serials = _sessions.Keys.ToList();
        }

        if (serials.Count > 0)
        {
            Log.Info($"正在关闭 {serials.Count} 个会话…");
            Task[] tasks = serials.Select(s => Task.Run(() => Detach(s))).ToArray();
            try
            {
                if (!Task.WaitAll(tasks, ShutdownTimeoutMs))
                {
                    Log.Error($"部分会话未在 {ShutdownTimeoutMs}ms 内关闭，继续退出流程。");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"批量关闭会话异常：{ex.Message}");
            }
        }

        try
        {
            _launcher.RemoveAllForwards();
        }
        catch (Exception ex)
        {
            Log.Warn($"清理 adb forward 异常：{ex.Message}");
        }

        Log.Info("全部会话已关闭。");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ShutdownAll();
    }

    /// <summary>会话状态变化 → 同步到已知设备信息并通知 UI。</summary>
    private void OnSessionStateChanged(string serial, DeviceState state)
    {
        DeviceInfo? info;
        lock (_gate)
        {
            _known.TryGetValue(serial, out info);
        }

        if (info == null)
        {
            return;
        }

        info.State = state;
        DeviceStatusUpdated?.Invoke(info);
    }

    /// <summary>会话错误 → 冒泡到 UI。</summary>
    private void OnSessionError(string serial, string message)
    {
        ErrorOccurred?.Invoke(serial, message);
    }
}
