using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace MultiScrcpy.Protocol;

/// <summary>
/// scrcpy 控制消息构造器（PC → 设备），架构文档 §5.4。
/// <para>
/// 所有整数一律 <b>大端序</b>，统一使用 <see cref="BinaryPrimitives"/>；
/// <b>禁止</b> <c>BitConverter</c>（默认小端且平台相关）。
/// </para>
/// 定长消息长度硬约束：keycode = 14、touch = 32、scroll = 21。
/// </summary>
public static class ControlMessages
{
    /// <summary>keycode 消息固定长度。</summary>
    public const int KEYCODE_MESSAGE_SIZE = 14;

    /// <summary>touch 消息固定长度。</summary>
    public const int TOUCH_MESSAGE_SIZE = 32;

    /// <summary>scroll 消息固定长度。</summary>
    public const int SCROLL_MESSAGE_SIZE = 21;

    /// <summary>text 消息头长度（type + u32 length）。</summary>
    public const int TEXT_HEADER_SIZE = 5;

    /// <summary>
    /// 构造按键注入消息。等价 Python <c>struct.pack('&gt;BBIII', ...)</c>，长度恒为 14。
    /// </summary>
    /// <param name="action">
    /// <see cref="ScrcpyConstants.ACTION_KEY_DOWN"/> 或 <see cref="ScrcpyConstants.ACTION_KEY_UP"/>。
    /// </param>
    /// <param name="keycode">Android keycode。</param>
    /// <param name="repeat">重复次数，默认 0。</param>
    /// <param name="metaState">修饰键状态，默认 0。</param>
    public static byte[] BuildKeycode(byte action, int keycode, uint repeat = 0,
                                      uint metaState = ScrcpyConstants.METASTATE_NONE)
    {
        byte[] buf = new byte[KEYCODE_MESSAGE_SIZE];
        Span<byte> s = buf.AsSpan();

        s[0] = ScrcpyConstants.TYPE_INJECT_KEYCODE;
        s[1] = action;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(2, 4), unchecked((uint)keycode));
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(6, 4), repeat);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(10, 4), metaState);

        Debug.Assert(buf.Length == KEYCODE_MESSAGE_SIZE, "keycode 消息必须是 14 字节");
        return buf;
    }

    /// <summary>
    /// 构造文本注入消息。等价 Python <c>struct.pack('&gt;BI', ...) + utf8</c>。
    /// <para>
    /// 超过 <see cref="ScrcpyConstants.TEXT_MAX_LENGTH"/> 字节时按 <b>完整 UTF-8 码点边界</b> 截断，
    /// 保证 4 字节 emoji 不会被切成半个字符；声明长度恒等于实际载荷长度。
    /// </para>
    /// </summary>
    public static byte[] BuildText(string? text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);

        if (utf8.Length > ScrcpyConstants.TEXT_MAX_LENGTH)
        {
            int cut = ScrcpyConstants.TEXT_MAX_LENGTH;
            // UTF-8 后续字节形如 10xxxxxx；若截断点落在后续字节上则整体回退到码点起始处
            while (cut > 0 && (utf8[cut] & 0xC0) == 0x80) cut--;
            Array.Resize(ref utf8, cut);
        }

        byte[] buf = new byte[TEXT_HEADER_SIZE + utf8.Length];
        buf[0] = ScrcpyConstants.TYPE_INJECT_TEXT;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)utf8.Length);
        utf8.CopyTo(buf, TEXT_HEADER_SIZE);

        Debug.Assert(buf.Length == TEXT_HEADER_SIZE + utf8.Length, "text 消息长度必须是 5 + 载荷长度");
        return buf;
    }

    /// <summary>
    /// 构造触摸注入消息。等价 Python <c>struct.pack('&gt;BBQiiHHHII', ...)</c>，长度恒为 32。
    /// <para>取值约定（与 Python 版同表）：</para>
    /// <list type="table">
    ///   <item><description>DOWN → pressure = 0xFFFF, actionButton = 1, buttons = 1</description></item>
    ///   <item><description>MOVE → pressure = 0xFFFF, actionButton = 0, buttons = 1</description></item>
    ///   <item><description>UP / CANCEL → pressure = 0, actionButton = 1, buttons = 0</description></item>
    /// </list>
    /// </summary>
    /// <param name="action">MotionEvent action。</param>
    /// <param name="x">视频帧坐标系 x。</param>
    /// <param name="y">视频帧坐标系 y。</param>
    /// <param name="w">当前视频帧宽。</param>
    /// <param name="h">当前视频帧高。</param>
    public static byte[] BuildTouch(byte action, int x, int y, int w, int h)
    {
        ushort pressure;
        uint actionButton;
        uint buttons;

        switch (action)
        {
            case ScrcpyConstants.ACTION_DOWN:
                pressure = 0xFFFF;
                actionButton = ScrcpyConstants.BUTTON_PRIMARY;
                buttons = ScrcpyConstants.BUTTON_PRIMARY;
                break;

            case ScrcpyConstants.ACTION_MOVE:
                pressure = 0xFFFF;
                actionButton = 0;
                buttons = ScrcpyConstants.BUTTON_PRIMARY;
                break;

            default: // ACTION_UP / ACTION_CANCEL
                pressure = 0;
                actionButton = ScrcpyConstants.BUTTON_PRIMARY;
                buttons = 0;
                break;
        }

        byte[] buf = new byte[TOUCH_MESSAGE_SIZE];
        Span<byte> s = buf.AsSpan();

        s[0] = ScrcpyConstants.TYPE_INJECT_TOUCH_EVENT;
        s[1] = action;
        BinaryPrimitives.WriteUInt64BigEndian(s.Slice(2, 8), ScrcpyConstants.POINTER_ID_MOUSE);
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(10, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(14, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(18, 2), ToU16(w));
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(20, 2), ToU16(h));
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(22, 2), pressure);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(24, 4), actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(28, 4), buttons);

        Debug.Assert(buf.Length == TOUCH_MESSAGE_SIZE, "touch 消息必须是 32 字节");
        return buf;
    }

    /// <summary>
    /// 构造滚轮注入消息。等价 Python <c>struct.pack('&gt;BiiHHhhI', ...)</c>，长度恒为 21。
    /// </summary>
    /// <param name="x">视频帧坐标系 x。</param>
    /// <param name="y">视频帧坐标系 y。</param>
    /// <param name="w">当前视频帧宽。</param>
    /// <param name="h">当前视频帧高。</param>
    /// <param name="hScroll">水平滚动量，[-1.0, 1.0]。</param>
    /// <param name="vScroll">垂直滚动量，[-1.0, 1.0]。</param>
    public static byte[] BuildScroll(int x, int y, int w, int h, float hScroll, float vScroll)
    {
        byte[] buf = new byte[SCROLL_MESSAGE_SIZE];
        Span<byte> s = buf.AsSpan();

        s[0] = ScrcpyConstants.TYPE_INJECT_SCROLL_EVENT;
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(1, 4), x);
        BinaryPrimitives.WriteInt32BigEndian(s.Slice(5, 4), y);
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(9, 2), ToU16(w));
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(11, 2), ToU16(h));
        BinaryPrimitives.WriteInt16BigEndian(s.Slice(13, 2), ScrcpyConstants.FloatToI16Fp(hScroll));
        BinaryPrimitives.WriteInt16BigEndian(s.Slice(15, 2), ScrcpyConstants.FloatToI16Fp(vScroll));
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(17, 4), ScrcpyConstants.BUTTON_PRIMARY);

        Debug.Assert(buf.Length == SCROLL_MESSAGE_SIZE, "scroll 消息必须是 21 字节");
        return buf;
    }

    /// <summary>把宽/高安全收敛到 u16 范围（协议字段是 u16）。</summary>
    private static ushort ToU16(int value)
    {
        if (value < 0) return 0;
        if (value > ushort.MaxValue) return ushort.MaxValue;
        return (ushort)value;
    }
}
