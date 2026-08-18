using System;

namespace MultiScrcpy.Core;

/// <summary>本项目所有业务异常的基类（架构文档 §9.3）。</summary>
public class ScrcpyPanelException : Exception
{
    public ScrcpyPanelException(string message) : base(message) { }

    public ScrcpyPanelException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>adb 调用失败（进程启动失败 / 超时 / 返回非零）。</summary>
public sealed class AdbException : ScrcpyPanelException
{
    public AdbException(string message) : base(message) { }

    public AdbException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>scrcpy-server 启动失败（jar 缺失 / forward 失败 / app_process 未起来）。</summary>
public sealed class ServerLaunchException : ScrcpyPanelException
{
    public ServerLaunchException(string message) : base(message) { }

    public ServerLaunchException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>协议层错误（连接被关闭 / 包头非法 / codec id 不支持）。</summary>
public sealed class ProtocolException : ScrcpyPanelException
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>解码器错误（FFmpeg 原生库缺失 / 上下文创建失败 / 不支持的 codec）。</summary>
public sealed class DecoderException : ScrcpyPanelException
{
    public DecoderException(string message) : base(message) { }

    public DecoderException(string message, Exception? inner) : base(message, inner) { }
}
