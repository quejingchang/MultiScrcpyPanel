using System;
using System.Diagnostics;

namespace MultiScrcpy.Core;

/// <summary>
/// 设备状态机（六态，架构文档 §8 T01-6 / PRD Q7）。
/// <para><b>Unauthorized 不可与 Offline 合并</b>：未授权设备需要专门的引导与重试入口。</para>
/// </summary>
public enum DeviceState
{
    /// <summary>离线 / 无权限 / adb 不可见。</summary>
    Offline = 0,

    /// <summary>已连接但未授权（手机上未点"允许"）。</summary>
    Unauthorized = 1,

    /// <summary>已发现且可用，尚未建立会话。</summary>
    Detected = 2,

    /// <summary>正在启动 server / 建立隧道 / 握手。</summary>
    Connecting = 3,

    /// <summary>正在投屏。</summary>
    Streaming = 4,

    /// <summary>会话出错。</summary>
    Error = 5
}

/// <summary>单台设备的可变状态快照。</summary>
public sealed class DeviceInfo
{
    public DeviceInfo(string serial, DeviceState state = DeviceState.Offline, string model = "")
    {
        Serial = serial ?? throw new ArgumentNullException(nameof(serial));
        State = state;
        Model = model ?? string.Empty;
    }

    /// <summary>adb 序列号，全局唯一键。</summary>
    public string Serial { get; }

    /// <summary>当前状态。</summary>
    public DeviceState State { get; set; }

    /// <summary>设备型号（<c>adb devices -l</c> 的 model 字段或 <c>getprop ro.product.model</c>）。</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>电量百分比；-1 表示未知（取电量失败绝不打断轮询）。</summary>
    public int Battery { get; set; } = -1;

    /// <summary>当前视频帧宽（来自 session packet / 解码帧）。</summary>
    public int VideoWidth { get; set; }

    /// <summary>当前视频帧高。</summary>
    public int VideoHeight { get; set; }

    /// <summary>握手阶段 server 返回的 64 字节设备名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>最近一次错误信息，用于状态栏回溯。</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>是否处于可投屏/已投屏状态。</summary>
    public bool IsOnline() =>
        State is DeviceState.Detected or DeviceState.Connecting or DeviceState.Streaming;

    /// <summary>是否低电量（&lt; 20%，且电量已知）。</summary>
    public bool IsLowBattery() => Battery >= 0 && Battery < 20;

    /// <summary>卡片标题显示文案：型号 + 序列号（+ 电量）。不显示 IMEI。</summary>
    public string DisplayTitle()
    {
        string model = string.IsNullOrWhiteSpace(Model) ? "未知型号" : Model;
        string battery = Battery >= 0 ? $" | 电量 {Battery}%" : string.Empty;
        return $"{model} | {Serial}{battery}";
    }

    /// <summary>把另一份扫描结果的可变字段合并进来（保留已有的非空信息）。</summary>
    /// <remarks>
    /// 状态字段的合并遵循「所有权」规则，这是修复 QA-BUG-01 的核心：
    /// <c>adb devices -l</c> 只能报出三态（device→Detected / unauthorized /
    /// offline），<b>无法感知</b>会话进展（Connecting / Streaming / Error）。
    /// 因此扫描态不得覆盖已有的会话态，否则会把「正在投屏」降级成「已发现」，
    /// 连带打断依赖 <c>State == Streaming</c> 的下游功能（如截图）。
    /// 但扫描到 Offline / Unauthorized 是比会话态更权威的坏消息（拔线 / 撤销授权），
    /// <b>必须</b>覆盖，否则卡片会永远停在「投影中」。
    /// 非会话态（Detected / Unauthorized / Offline）之间则照常覆盖，
    /// 这是「重新授权 / 重插后自动接入」链路的前提。
    /// </remarks>
    public void MergeFrom(DeviceInfo other)
    {
        if (other == null) return;

        bool sessionOwned = State is DeviceState.Connecting
                                     or DeviceState.Streaming
                                     or DeviceState.Error;
        bool scanIsAuthoritative = other.State is DeviceState.Offline
                                                or DeviceState.Unauthorized;
        if (!sessionOwned || scanIsAuthoritative) State = other.State;

        if (!string.IsNullOrWhiteSpace(other.Model)) Model = other.Model;
        if (other.Battery >= 0) Battery = other.Battery;
    }

    public override string ToString() => $"{Serial}({State})";
}

/// <summary>一次 scrcpy-server 启动所占用的资源句柄。</summary>
public sealed record class TunnelHandle(string Serial, string Scid, int Port)
{
    /// <summary><see cref="ServerExitCode"/> 的哨兵值：进程尚未退出。</summary>
    public const int ServerRunning = int.MinValue;

    /// <summary>承载 <c>adb shell app_process</c> 的进程；启动失败时可能为 null。</summary>
    public Process? ServerProcess { get; set; }

    /// <summary>
    /// 设备端 server 的 stdout + stderr 环形缓冲。
    /// <para>握手失败时回填到异常信息，让用户看到「设备端到底报了什么错」。</para>
    /// </summary>
    public ServerLogBuffer ServerLog { get; } = new();

    /// <summary>
    /// 承载进程的退出码；<see cref="ServerRunning"/> 表示尚未退出。
    /// <para>由 <c>Process.Exited</c> 回调写入，会话线程读取，用 <c>Volatile</c> 语义足够。</para>
    /// </summary>
    public int ServerExitCode { get; set; } = ServerRunning;

    /// <summary>承载进程是否已退出。</summary>
    public bool ServerExited => ServerExitCode != ServerRunning;

    /// <summary>
    /// 是否正在主动关闭本会话。
    /// <para>置位后 <c>Process.Exited</c> 回调不再把非零退出码当成异常（我们自己 Kill 的）。</para>
    /// </summary>
    public bool ShuttingDown { get; set; }

    /// <summary>abstract socket 名称。</summary>
    public string AbstractName => $"scrcpy_{Scid}";
}
