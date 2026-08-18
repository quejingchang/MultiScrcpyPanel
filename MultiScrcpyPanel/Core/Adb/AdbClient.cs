using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MultiScrcpy.Core.Adb;

/// <summary>
/// 对外部 <c>adb.exe</c> 的薄封装（架构文档 §8 T02）。
/// <para>
/// 硬性约束：
/// <list type="number">
///   <item><description>参数一律用 <c>ProcessStartInfo.ArgumentList</c> 逐项添加，禁止手拼字符串。</description></item>
///   <item><description>输出一律用 <c>OutputDataReceived</c> + <c>BeginOutputReadLine()</c> 异步收集，
///   禁止 <c>ReadToEnd()</c> 后 <c>WaitForExit()</c>（经典死锁）。</description></item>
///   <item><description><c>CreateNoWindow = true, UseShellExecute = false</c>，避免黑窗闪烁。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class AdbClient
{
    /// <summary>
    /// adb 未配置时对外统一使用的清晰中文提示。
    /// <para>三处守卫（<see cref="Run"/> / <see cref="SpawnShell"/> / <c>CreateStartInfo</c>）共用同一文案，
    /// 避免用户看到底层的英文 <c>Win32Exception: 系统找不到指定的文件</c>。</para>
    /// </summary>
    internal const string AdbNotConfiguredMessage =
        "adb 未配置：请安装 Android Platform-Tools 并加入 PATH，" +
        "或在 config/settings.json 的 AdbPath 中指定 adb.exe 完整路径。";

    /// <summary>
    /// 匹配设备端 scrcpy-server 进程的 <c>pkill -f</c> 模式。
    /// <para>用 Java 主类全名而不是 <c>app_process</c>：后者是 Android 系统进程的通用宿主，
    /// 误杀会导致设备异常。</para>
    /// </summary>
    internal const string ServerProcessPattern = "com.genymobile.scrcpy.Server";

    private readonly string _adbPath;
    private readonly int _defaultTimeoutMs;

    /// <summary>创建 adb 客户端。</summary>
    /// <param name="adbPath">
    /// adb 可执行文件的完整路径；<b>允许为空</b>，表示 adb 未配置。
    /// <para>此处刻意<b>不再回退成裸名 <c>"adb"</c></b>：裸名会在 <c>Process.Start</c> 时抛出晦涩的
    /// 英文 <c>Win32Exception</c>，把「adb 没装」这一明确原因掩盖掉。
    /// 空路径统一由 <see cref="IsAvailable"/> 暴露为「不可用」状态。</para>
    /// </param>
    /// <param name="defaultTimeoutMs">默认命令超时毫秒；&lt;= 0 使用 10000。</param>
    public AdbClient(string? adbPath, int defaultTimeoutMs = 10000)
    {
        _adbPath = adbPath ?? string.Empty; // 允许为空，表示 adb 未配置；由 IsAvailable 暴露状态
        _defaultTimeoutMs = defaultTimeoutMs > 0 ? defaultTimeoutMs : 10000;
    }

    /// <summary>当前使用的 adb 可执行文件路径；未配置时为空串。</summary>
    public string AdbPath => _adbPath;

    /// <summary>adb 是否已配置可用（路径非空且非仅裸名占位）。</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_adbPath);

    /// <summary>
    /// 网络/可移动盘自愈：若 adb 位于网络盘（如 RaiDrive 映射的 WebDAV 盘符）或可移动盘，
    /// Windows 无法可靠地对该可执行映像做内存映射分页，子进程启动会失败并弹出
    /// <c>0xC0000006</c>（STATUS_IN_PAGE_ERROR，应用程序无法正常启动）。
    /// <para>此函数把 <c>adb.exe</c> 及其同目录原生 DLL（AdbWinApi.dll / AdbWinUsbApi.dll 等）
    /// 复制到本地缓存目录 <c>%LOCALAPPDATA%\MultiScrcpy\adb</c>，返回本地副本路径；
    /// 本地副本与源大小/时间戳一致时直接复用，不重复复制。非网络盘则原样返回。</para>
    /// <para>任何异常都降级为「返回原路径」，不影响既有 PATH/本地部署路径。</para>
    /// </summary>
    public static string LocalizeIfRemote(string adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath)) return adbPath;

        string? root = Path.GetPathRoot(adbPath);
        if (string.IsNullOrEmpty(root)) return adbPath;

        DriveType dt;
        try
        {
            dt = new DriveInfo(root).DriveType;
        }
        catch (Exception)
        {
            return adbPath;
        }

        if (dt != DriveType.Network && dt != DriveType.Removable) return adbPath;

        try
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiScrcpy", "adb");
            Directory.CreateDirectory(cacheDir);

            string srcDir = Path.GetDirectoryName(adbPath)!;
            string exeName = Path.GetFileName(adbPath);

            // adb.exe 与其同目录的原生 DLL 一并复制：adb 运行时会从自身目录查找 AdbWinApi.dll 等
            var sources = new List<string>(Directory.EnumerateFiles(srcDir, "*.dll"))
            {
                adbPath
            };

            foreach (string src in sources)
            {
                string dest = Path.Combine(cacheDir, Path.GetFileName(src));
                var s = new FileInfo(src);
                var d = File.Exists(dest) ? new FileInfo(dest) : null;
                if (d == null || d.Length != s.Length || d.LastWriteTimeUtc < s.LastWriteTimeUtc)
                {
                    File.Copy(src, dest, overwrite: true);
                }
            }

            string localExe = Path.Combine(cacheDir, exeName);
            Log.Info($"adb 位于网络/可移动盘，已缓存到本地以规避 0xC0000006：{localExe}");
            return localExe;
        }
        catch (Exception ex)
        {
            Log.Warn($"adb 本地化缓存失败，仍使用原路径（可能触发 0xC0000006）：{ex.Message}");
            return adbPath;
        }
    }


    /// <summary>
    /// 同步执行一条 adb 命令并返回 stdout。
    /// </summary>
    /// <param name="args">adb 子命令与参数（不含 <c>-s serial</c>）。</param>
    /// <param name="serial">设备序列号；非空时自动前置 <c>-s serial</c>。</param>
    /// <param name="timeoutMs">超时毫秒；&lt;= 0 使用默认值。</param>
    /// <exception cref="AdbException">adb 未配置 / 进程启动失败 / 超时 / 退出码非零。</exception>
    public string Run(IReadOnlyList<string> args, string? serial = null, int timeoutMs = 0)
    {
        if (!IsAvailable)
        {
            throw new AdbException(AdbNotConfiguredMessage);
        }

        var psi = CreateStartInfo(args, serial);
        int effectiveTimeout = timeoutMs > 0 ? timeoutMs : _defaultTimeoutMs;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = false };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data);
        };

        try
        {
            if (!process.Start())
            {
                throw new AdbException($"无法启动 adb 进程：{_adbPath}");
            }
        }
        catch (AdbException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AdbException($"无法启动 adb 进程：{_adbPath}（{ex.Message}）", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(effectiveTimeout))
        {
            TryKill(process);
            throw new AdbException($"adb 命令超时（{effectiveTimeout}ms）：{DescribeCommand(args, serial)}");
        }

        // 保证异步输出全部刷入（无参重载会等待输出流结束）
        try { process.WaitForExit(); } catch { /* 已退出 */ }

        string outText;
        string errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(errText) ? outText : errText;
            throw new AdbException(
                $"adb 命令失败（退出码 {process.ExitCode}）：{DescribeCommand(args, serial)}{Environment.NewLine}{detail.Trim()}");
        }

        return outText;
    }

    /// <summary>执行 <c>adb devices -l</c> 并解析设备列表。</summary>
    public IReadOnlyList<DeviceInfo> Devices()
    {
        string output = Run(new[] { "devices", "-l" });
        return ParseDevicesOutput(output);
    }

    /// <summary>
    /// 解析 <c>adb devices -l</c> 输出（纯函数，可无头单测）。
    /// <para>
    /// 跳过标题行、空行与 <c>* daemon ... *</c> 噪声行；
    /// 状态映射（大小写不敏感）：<c>device → Detected</c>、<c>unauthorized → Unauthorized</c>、其余 → <c>Offline</c>。
    /// 顺带从 <c>model:XXX</c> 字段取型号，省一次 getprop。
    /// </para>
    /// </summary>
    public static IReadOnlyList<DeviceInfo> ParseDevicesOutput(string? output)
    {
        var result = new List<DeviceInfo>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        string[] lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("*", StringComparison.Ordinal)) continue;                       // * daemon ... *
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("adb server", StringComparison.OrdinalIgnoreCase)) continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string serial = parts[0];
            string stateText = parts[1].ToLowerInvariant();

            DeviceState state = stateText switch
            {
                "device" => DeviceState.Detected,
                "unauthorized" => DeviceState.Unauthorized,
                _ => DeviceState.Offline
            };

            string model = string.Empty;
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                {
                    model = parts[i].Substring("model:".Length).Replace('_', ' ').Trim();
                    break;
                }
            }

            result.Add(new DeviceInfo(serial, state, model));
        }

        return result;
    }

    /// <summary>推送本地文件到设备。</summary>
    public void Push(string serial, string localPath, string remotePath)
    {
        Run(new[] { "push", localPath, remotePath }, serial, timeoutMs: 60000);
    }

    /// <summary>建立 forward 隧道：<c>tcp:port → localabstract:scrcpy_&lt;scid&gt;</c>。</summary>
    public void Forward(string serial, int port, string scid)
    {
        Run(new[] { "forward", $"tcp:{port}", $"localabstract:scrcpy_{scid}" }, serial);
    }

    /// <summary>移除指定端口的 forward 隧道（不使用 --remove-all，避免误删其他工具的隧道）。</summary>
    public void RemoveForward(string serial, int port)
    {
        Run(new[] { "forward", "--remove", $"tcp:{port}" }, serial);
    }

    /// <summary>
    /// 尽力移除指定端口的 forward 隧道：<b>隧道本来就不存在也算成功</b>，绝不抛出。
    /// <para>用于「建立新隧道之前先拆掉可能残留的同端口隧道」，
    /// 以及关闭流程中的兜底清理——这两处都不该因为「没有可删的」而报错。</para>
    /// </summary>
    /// <returns>是否确实删掉了一条隧道。</returns>
    public bool RemoveForwardQuiet(string serial, int port)
    {
        try
        {
            Run(new[] { "forward", "--remove", $"tcp:{port}" }, serial, 5000);
            return true;
        }
        catch (Exception ex)
        {
            // adb 在「该 forward 不存在」时返回非零，这是最常见的情况，只记 Debug。
            Log.Debug($"[{serial}] 移除 forward tcp:{port} 未生效（多半本就不存在）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 尽力杀掉<b>该设备上</b>残留的 scrcpy-server 进程，绝不抛出。
    /// <para>
    /// 必要性：<c>adb shell &lt;cmd&gt;</c> 返回的是<b>本机</b> adb 客户端进程，
    /// Kill 它并不会杀死设备端的 <c>app_process</c>。旧 server 若继续存活，
    /// 会与新 server 争抢 <c>localabstract:scrcpy_*</c>，导致新连接读到 EOF。
    /// </para>
    /// <para>命令按 <c>-s serial</c> 限定，不会波及其他设备。</para>
    /// </summary>
    /// <returns>是否至少有一条清理命令执行成功。</returns>
    public bool KillRemoteServers(string serial)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }

        // pkill 在「没有匹配进程」时返回 1，属于正常情况，因此全部走静默通道。
        string[][] attempts =
        {
            new[] { "shell", "pkill", "-f", ServerProcessPattern },
            new[] { "shell", "pkill", "-9", "-f", ServerProcessPattern }
        };

        bool killed = false;
        foreach (string[] args in attempts)
        {
            try
            {
                Run(args, serial, 5000);
                killed = true;
                break; // 第一条成功即可，不必再发 -9
            }
            catch (Exception ex)
            {
                Log.Debug($"[{serial}] 清理设备端残留 server（{string.Join(' ', args)}）未生效：{ex.Message}");
            }
        }

        if (killed)
        {
            Log.Info($"[{serial}] 已清理设备端残留的 scrcpy-server 进程。");
        }

        return killed;
    }

    /// <summary>读取设备型号 <c>getprop ro.product.model</c>；失败返回空串。</summary>
    public string GetModel(string serial)
    {
        try
        {
            return Run(new[] { "shell", "getprop", "ro.product.model" }, serial, 5000).Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] 读取型号失败：{ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>读取电量百分比；<b>失败返回 -1，绝不抛异常打断轮询</b>。</summary>
    public int GetBattery(string serial)
    {
        try
        {
            string output = Run(new[] { "shell", "dumpsys", "battery" }, serial, 5000);
            return ParseBatteryLevel(output);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] 读取电量失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>解析 <c>dumpsys battery</c> 的 <c>level:</c> 行（纯函数，可单测）；失败返回 -1。</summary>
    public static int ParseBatteryLevel(string? dumpsysOutput)
    {
        if (string.IsNullOrWhiteSpace(dumpsysOutput)) return -1;

        string[] lines = dumpsysOutput.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("level:", StringComparison.OrdinalIgnoreCase)) continue;

            string value = line.Substring("level:".Length).Trim();
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
                && level >= 0 && level <= 100)
            {
                return level;
            }
        }

        return -1;
    }

    /// <summary>
    /// 启动一个长驻的 <c>adb shell &lt;command&gt;</c> 进程（用于拉起 scrcpy-server）。
    /// 返回的进程已开启异步输出读取，调用方需挂接事件后自行 Kill/Dispose。
    /// </summary>
    /// <exception cref="AdbException">adb 未配置。</exception>
    /// <exception cref="ServerLaunchException">进程启动失败。</exception>
    public Process SpawnShell(string serial, string command)
    {
        if (!IsAvailable)
        {
            throw new AdbException(AdbNotConfiguredMessage);
        }

        // 整条 shell 命令作为一个 ArgumentList 元素传入，内部空格由 adb 传给设备 shell
        var psi = CreateStartInfo(new[] { "shell", command }, serial);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new ServerLaunchException($"[{serial}] 无法启动 adb shell 进程");
            }
        }
        catch (ServerLaunchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new ServerLaunchException($"[{serial}] 无法启动 adb shell 进程：{ex.Message}", ex);
        }

        return process;
    }

    /// <summary>尝试重新连接设备（未授权重试用），失败只记日志。</summary>
    public void Reconnect(string serial)
    {
        try
        {
            Run(new[] { "reconnect" }, serial, 8000);
        }
        catch (Exception ex)
        {
            Log.Warn($"[{serial}] adb reconnect 失败：{ex.Message}");
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args, string? serial)
    {
        // 兜底守卫：任何路径下都不允许用空/裸名启动进程（否则只会得到英文 Win32Exception）
        if (string.IsNullOrWhiteSpace(_adbPath))
        {
            throw new AdbException(AdbNotConfiguredMessage);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            // 固定工作目录，消除「working directory」不确定性带来的报错噪声
            WorkingDirectory = AppContext.BaseDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(serial))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial!);
        }

        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    private static string DescribeCommand(IReadOnlyList<string> args, string? serial)
    {
        var sb = new StringBuilder("adb");
        if (!string.IsNullOrWhiteSpace(serial)) sb.Append(" -s ").Append(serial);
        foreach (string a in args) sb.Append(' ').Append(a);
        return sb.ToString();
    }
}
