using System;

namespace MultiScrcpy.Protocol;

/// <summary>
/// scrcpy v4.0 协议常量与定点数转换（架构文档 §5.4）。
/// <para>
/// 本类型属于基础层，<b>仅允许依赖 BCL</b>：禁止 WinForms / FFmpeg.AutoGen / Socket /
/// 本项目其他命名空间。所有取值与 Python 版逐字节一致。
/// </para>
/// 命名沿用 scrcpy 官方的 UPPER_SNAKE_CASE，便于与 scrcpy 源码及 Python 版逐字对照。
/// </summary>
public static class ScrcpyConstants
{
    // ---------------- 控制消息类型 ----------------

    /// <summary>注入按键事件（14 字节）。</summary>
    public const byte TYPE_INJECT_KEYCODE = 0;

    /// <summary>注入文本（5 + N 字节）。</summary>
    public const byte TYPE_INJECT_TEXT = 1;

    /// <summary>注入触摸事件（32 字节）。</summary>
    public const byte TYPE_INJECT_TOUCH_EVENT = 2;

    /// <summary>注入滚轮事件（21 字节）。</summary>
    public const byte TYPE_INJECT_SCROLL_EVENT = 3;

    // ---------------- KeyEvent action ----------------

    public const byte ACTION_KEY_DOWN = 0;
    public const byte ACTION_KEY_UP = 1;

    // ---------------- MotionEvent action ----------------

    public const byte ACTION_DOWN = 0;
    public const byte ACTION_UP = 1;
    public const byte ACTION_MOVE = 2;
    public const byte ACTION_CANCEL = 3;

    // ---------------- Android keycode ----------------

    public const int KEYCODE_HOME = 3;
    public const int KEYCODE_BACK = 4;
    public const int KEYCODE_POWER = 26;
    public const int KEYCODE_VOLUME_UP = 24;
    public const int KEYCODE_VOLUME_DOWN = 25;

    /// <summary>Recent Apps（最近任务）。</summary>
    public const int KEYCODE_APP_SWITCH = 187;

    /// <summary>Android KEYCODE_ENTER（输入法确认）。</summary>
    public const int KEYCODE_ENTER = 66;

    /// <summary>Android KEYCODE_MENU。</summary>
    public const int KEYCODE_MENU = 82;

    /// <summary>Android KEYCODE_CAMERA。</summary>
    public const int KEYCODE_CAMERA = 27;

    /// <summary>Android KEYCODE_SEARCH。</summary>
    public const int KEYCODE_SEARCH = 84;

    /// <summary>Android KEYCODE_DPAD_CENTER。</summary>
    public const int KEYCODE_DPAD_CENTER = 23;

    // ---------------- 鼠标语义 ----------------

    /// <summary>鼠标指针 id，固定为 (uint64)-1。</summary>
    public const ulong POINTER_ID_MOUSE = 0xFFFFFFFFFFFFFFFFUL;

    /// <summary>AMOTION_EVENT_BUTTON_PRIMARY。</summary>
    public const uint BUTTON_PRIMARY = 1;

    /// <summary>无修饰键。</summary>
    public const uint METASTATE_NONE = 0;

    // ---------------- codec id（视频流首 4 字节）----------------

    public const uint CODEC_H264 = 0x68323634;
    public const uint CODEC_H265 = 0x68323635;
    public const uint CODEC_AV1 = 0x00617631;

    // ---------------- 视频流 ----------------

    /// <summary>每个视频流包的固定头长度。</summary>
    public const int STREAM_HEADER_SIZE = 12;

    /// <summary>握手阶段设备名字段长度。</summary>
    public const int DEVICE_NAME_SIZE = 64;

    /// <summary>scrcpy 服务端对 TYPE_INJECT_TEXT 的最大长度限制（字节）。</summary>
    public const int TEXT_MAX_LENGTH = 300;

    // ---------------- 定点数转换 ----------------

    /// <summary>
    /// [0.0, 1.0] → [0, 0xFFFF]（对应 Python 版 <c>float_to_u16fp</c>）。
    /// 越界值先 clamp；C# 的 (int) 与 Python 的 int() 均向零截断，行为一致。
    /// </summary>
    public static ushort FloatToU16Fp(float value)
    {
        float v = Math.Clamp(value, 0f, 1f);
        int x = (int)(v * (1 << 16));
        return x >= 0xFFFF ? (ushort)0xFFFF : (ushort)x;
    }

    /// <summary>
    /// [-1.0, 1.0] → [-0x8000, 0x7FFF]（对应 Python 版 <c>float_to_i16fp</c>）。
    /// </summary>
    public static short FloatToI16Fp(float value)
    {
        float v = Math.Clamp(value, -1f, 1f);
        int x = (int)(v * (1 << 15));
        if (x >= 0x7FFF) return 0x7FFF;
        if (x <= -0x8000) return unchecked((short)-0x8000);
        return (short)x;
    }

    /// <summary>
    /// 生成 scrcpy 会话 id：31 位随机数格式化为 8 位小写 hex（架构文档 §9.6）。
    /// </summary>
    public static string GenerateScid() => Random.Shared.Next(0, int.MaxValue).ToString("x8");

    /// <summary>codec id → 可读名称，仅用于日志。</summary>
    public static string CodecName(uint codecId) => codecId switch
    {
        CODEC_H264 => "h264",
        CODEC_H265 => "h265",
        CODEC_AV1 => "av1",
        _ => $"0x{codecId:X8}"
    };
}
