using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using MultiScrcpy.Protocol;

namespace MultiScrcpy.Core.Adb;

/// <summary>
/// scrcpy-server 启动器（架构文档 §5.1 / §8 T02-3）。
/// <para>流程：清理残留 → push jar → adb forward → adb shell app_process 拉起 server。</para>
/// <para><b>命令逐字对齐架构文档 §5.1</b>；<c>tunnel_forward=true</c> 不可省略。</para>
/// </summary>
public sealed class ScrcpyServerLauncher
{
    /// <summary>设备端 jar 落地路径。</summary>
    public const string RemoteJarPath = "/data/local/tmp/scrcpy-server.jar";

    private readonly AdbClient _adb;
    private readonly AppConfig _cfg;
    private readonly PortAllocator _ports;

    public ScrcpyServerLauncher(AdbClient adb, AppConfig cfg)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _ports = new PortAllocator(cfg.PortBase);
    }

    /// <summary>端口分配器（供诊断/测试）。</summary>
    public PortAllocator Ports => _ports;

    /// <summary>底层 adb 客户端（供会话执行 <c>screencap</c> 等命令）。</summary>
    public AdbClient Adb => _adb;

    /// <summary>
    /// 确保 jar 已推送到设备。
    /// <para>⚡ 性能优化（P0）：先探测设备端是否已有<b>同名且同大小</b>的 jar，
    /// 已存在则跳过 push（重连 / 多设备启动每台省 0.5–2s，并避免无谓的设备端写入）。
    /// 探测失败或大小不一致时保守地重新推送。</para>
    /// </summary>
    /// <exception cref="ServerLaunchException">本地 jar 不存在或 push 失败。</exception>
    public void EnsureJarPushed(string serial)
    {
        string jar = _cfg.ResolveServerJar();
        if (!File.Exists(jar))
        {
            throw new ServerLaunchException(
                $"未找到 scrcpy-server jar：{jar}{Environment.NewLine}" +
                "请运行 tools\\fetch_scrcpy_server.ps1 下载 v4.0，或手动放置到 assets\\ 目录。");
        }

        long localSize = new FileInfo(jar).Length;
        if (IsRemoteJarUpToDate(serial, localSize))
        {
            Log.Info($"[{serial}] 设备端 jar 已存在且大小一致（{localSize} 字节），跳过推送。");
            return;
        }

        PushJar(serial, jar);
    }

    /// <summary>探测设备端 jar 是否存在且字节数与本地一致；任何异常都返回 false（保守重推）。</summary>
    private bool IsRemoteJarUpToDate(string serial, long localSize)
    {
        if (!_adb.IsAvailable || string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }

        try
        {
            // 单条 shell：文件存在则输出字节数，否则输出 __MISSING__，stat 异常则输出 __STATERR__。
            // 整体 exit 0，避免 Run 因 stat 失败抛出；最终由解析兜底为「需重推」。
            string probe = _adb.Run(
                new[]
                {
                    "shell",
                    $"if [ -f {RemoteJarPath} ]; then stat -c %s {RemoteJarPath} || echo __STATERR__; else echo __MISSING__; fi"
                },
                serial,
                5000).Trim();

            if (probe == "__MISSING__" || probe == "__STATERR__")
            {
                return false;
            }

            return long.TryParse(probe, out long remoteSize) && remoteSize == localSize;
        }
        catch (Exception ex)
        {
            Log.Debug($"[{serial}] 探测设备端 jar 失败（将重新推送）：{ex.Message}");
            return false;
        }
    }

    private void PushJar(string serial, string jar)
    {
        try
        {
            _adb.Push(serial, jar, RemoteJarPath);
            Log.Info($"[{serial}] 已推送 server jar → {RemoteJarPath}");
        }
        catch (Exception ex)
        {
            throw new ServerLaunchException($"[{serial}] 推送 scrcpy-server jar 失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 拼接 <c>adb shell</c> 后的整条命令（逐字对齐架构文档 §5.1，可单测）。
    /// </summary>
    public string BuildServerCommand(string scid)
    {
        var sb = new StringBuilder();
        sb.Append("CLASSPATH=").Append(RemoteJarPath).Append(' ');
        sb.Append("app_process / com.genymobile.scrcpy.Server ");
        sb.Append(_cfg.ServerVersion).Append(' ');                 // 版本号必须是第一个位置参数
        sb.Append("scid=").Append(scid).Append(' ');
        sb.Append("log_level=info ");
        sb.Append("video=true audio=false control=true ");
        sb.Append("tunnel_forward=").Append(_cfg.TunnelForward ? "true" : "false").Append(' ');
        sb.Append("video_codec=").Append(_cfg.VideoCodec).Append(' ');
        sb.Append("max_size=").Append(_cfg.MaxSize).Append(' ');
        sb.Append("video_bit_rate=").Append(_cfg.VideoBitRate).Append(' ');
        sb.Append("max_fps=").Append(_cfg.MaxFps).Append(' ');
        sb.Append("cleanup=true");
        return sb.ToString();
    }

    /// <summary>
    /// 启动一次 server 会话：<b>清理残留</b> → 分配 scid/端口 → push jar → forward → 拉起 app_process。
    /// </summary>
    /// <exception cref="ServerLaunchException">任一步骤失败（已回滚端口与隧道）。</exception>
    public TunnelHandle Launch(string serial)
    {
        // ⭐ 起新 server 之前先拆干净上一次：
        //    用户反复点「刷新设备 / 全部重连」时，若设备端旧 server 还活着，
        //    新旧两个 server 会争抢 localabstract:scrcpy_*，新连接极易读到 EOF。
        CleanupStaleSession(serial);

        string scid = ScrcpyConstants.GenerateScid();
        int port = _ports.Acquire();
        var handle = new TunnelHandle(serial, scid, port);
        bool forwarded = false;

        try
        {
            EnsureJarPushed(serial);

            // 同一端口上可能残留着上一轮会话（或上一次进程崩溃）留下的 forward，
            // 它仍指向一个已经死掉的 scid。必须先删掉，否则新 forward 无法覆盖语义。
            _adb.RemoveForwardQuiet(serial, port);

            _adb.Forward(serial, port, scid);
            forwarded = true;
            Log.Info($"[{serial}] 已建立隧道 tcp:{port} → localabstract:{handle.AbstractName}");

            string command = BuildServerCommand(scid);
            Log.Debug($"[{serial}] server 命令：{command}");

            Process process = _adb.SpawnShell(serial, command);
            handle.ServerProcess = process;
            HookServerLogs(serial, handle, process);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            Log.Info($"[{serial}] scrcpy-server v{_cfg.ServerVersion} 已启动（scid={scid}）");
            return handle;
        }
        catch (Exception ex)
        {
            // 回滚：先清隧道再还端口，每步独立 try/catch
            if (forwarded)
            {
                _adb.RemoveForwardQuiet(serial, port);
            }

            try { _ports.Release(port); } catch { /* 尽力清理 */ }

            if (ex is ServerLaunchException) throw;
            throw new ServerLaunchException($"[{serial}] 启动 scrcpy-server 失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 清理某台设备上可能残留的上一次会话（<b>尽力而为，绝不抛出</b>）。
    /// <para>目前只做一件事：杀掉设备端残留的 scrcpy-server 进程。
    /// 端口/隧道的清理按端口粒度在 <see cref="Launch"/> 与 <see cref="Shutdown"/> 中进行，
    /// 避免 <c>--remove-all</c> 那种连带伤害。</para>
    /// </summary>
    public void CleanupStaleSession(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return;
        }

        try
        {
            _adb.KillRemoteServers(serial);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] 清理设备端残留 server 异常（已忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 关闭一次 server 会话：杀本机 adb shell → <b>杀设备端 server</b> → 移除 forward → 归还端口。
    /// <b>每一步独立 try/catch，任何一步失败不阻断其余清理；可重复调用。</b>
    /// </summary>
    public void Shutdown(TunnelHandle? handle)
    {
        if (handle == null) return;

        // 先置位：接下来 Kill 触发的 Process.Exited 不该被当成「server 异常退出」。
        handle.ShuttingDown = true;

        Process? process = handle.ServerProcess;
        handle.ServerProcess = null;

        if (process != null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"[{handle.Serial}] 结束 server 进程失败：{ex.Message}");
            }

            try { process.WaitForExit(2000); }
            catch (Exception ex) { Log.Warn($"[{handle.Serial}] 等待 server 进程退出失败：{ex.Message}"); }

            try { process.Dispose(); }
            catch { /* 尽力清理 */ }
        }

        // ⭐ Kill 掉的只是本机 adb 客户端进程，设备端 app_process 仍在跑，必须单独收拾。
        CleanupStaleSession(handle.Serial);

        if (_adb.RemoveForwardQuiet(handle.Serial, handle.Port))
        {
            Log.Info($"[{handle.Serial}] 已移除隧道 tcp:{handle.Port}");
        }

        try { _ports.Release(handle.Port); }
        catch (Exception ex) { Log.Warn($"[{handle.Serial}] 归还端口失败：{ex.Message}"); }
    }

    /// <summary>
    /// 兜底清理：调用 <c>adb forward --remove-all</c> 移除所有本机端口转发。
    /// 供 <c>DeviceManager.ShutdownAll()</c> 在逐个 <see cref="Shutdown"/> 之后调用，
    /// 确保进程退出后 <c>adb forward --list</c> 为空（PRD 退出清理要求）。
    /// <para><b>只在整体退出时使用</b>：单设备场景一律走按端口的 <see cref="Shutdown"/>，
    /// 避免误删其他工具/其他设备的隧道。</para>
    /// </summary>
    public void RemoveAllForwards()
    {
        try
        {
            _adb.Run(new[] { "forward", "--remove-all" });
            Log.Info("已执行 adb forward --remove-all。");
        }
        catch (Exception ex)
        {
            Log.Warn($"adb forward --remove-all 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把 server 的 <b>stdout 与 stderr 每一行</b>都转存到 <see cref="TunnelHandle.ServerLog"/> 并写日志，
    /// 同时监听进程退出。
    /// <para>
    /// scrcpy-server 初始化失败（屏幕捕获 / 编码器 / 权限）时异常打在 <b>stderr</b>；
    /// 只抓 stdout 会让真实原因彻底消失，用户只能看到客户端侧一句「对端关闭连接」。
    /// </para>
    /// </summary>
    private static void HookServerLogs(string serial, TunnelHandle handle, Process process)
    {
        process.OutputDataReceived += (_, e) => AppendServerLine(serial, handle, e.Data, isStdErr: false);
        process.ErrorDataReceived += (_, e) => AppendServerLine(serial, handle, e.Data, isStdErr: true);
        process.Exited += (_, _) => OnServerProcessExited(serial, handle, process);
    }

    /// <summary>写入一行 server 输出：进环形缓冲 + 按严重程度分级打日志。</summary>
    private static void AppendServerLine(string serial, TunnelHandle handle, string? raw, bool isStdErr)
    {
        try
        {
            string line = handle.ServerLog.Append(raw);
            if (line.Length == 0)
            {
                return;
            }

            string channel = isStdErr ? "server:err" : "server:out";

            if (ServerLogBuffer.LooksLikeError(line))
            {
                Log.Error($"[{serial}][{channel}] {line}");
            }
            else if (isStdErr)
            {
                // stderr 上的非错误行多为 scrcpy 的 INFO/WARN 日志，不必渲染成红色错误。
                Log.Info($"[{serial}][{channel}] {line}");
            }
            else
            {
                Log.Info($"[{serial}][{channel}] {line}");
            }
        }
        catch (Exception ex)
        {
            // 日志回调绝不允许抛回 Process 的线程池线程。
            Log.Debug($"[{serial}] 记录 server 输出异常：{ex.Message}");
        }
    }

    /// <summary>承载进程退出：记录退出码，主动关闭时降级为 Info。</summary>
    private static void OnServerProcessExited(string serial, TunnelHandle handle, Process process)
    {
        int code;
        try { code = process.ExitCode; }
        catch { code = -1; }

        handle.ServerExitCode = code;

        if (handle.ShuttingDown)
        {
            Log.Info($"[{serial}] server 承载进程已退出（主动关闭，退出码 {code}）。");
            return;
        }

        if (code == 0)
        {
            Log.Warn($"[{serial}] server 承载进程提前正常退出（退出码 0）——设备端 scrcpy-server 已结束。");
            return;
        }

        string tail = handle.ServerLog.Describe();
        string detail = tail.Length > 0
            ? $"{Environment.NewLine}设备端 server 最近输出：{Environment.NewLine}{tail}"
            : "（设备端 server 没有任何输出）";

        Log.Error($"[{serial}] server 承载进程异常退出，退出码 {code}。{detail}");
    }
}
