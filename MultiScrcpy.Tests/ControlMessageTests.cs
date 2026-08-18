using System;
using System.Linq;
using System.Text;

using MultiScrcpy.Protocol;

using Xunit;

namespace MultiScrcpy.Tests;

/// <summary>
/// 控制消息（PC → 设备）字节级单测，对应架构文档 §5.4 与 §9.2 的 T01 质量门禁。
/// <para>
/// 这些断言是整个项目的地基：<b>任何一位错位都会让设备端静默丢弃消息或误触发操作</b>，
/// 所以全部用「硬编码期望字节数组」比对，而不是用被测代码自身的常量反推。
/// </para>
/// </summary>
public sealed class ControlMessageTests
{
    // ---------------------------------------------------------------- 长度硬约束

    [Fact]
    public void 消息长度必须严格等于协议规定值()
    {
        Assert.Equal(14, ControlMessages.BuildKeycode(ScrcpyConstants.ACTION_KEY_DOWN, ScrcpyConstants.KEYCODE_HOME).Length);
        Assert.Equal(32, ControlMessages.BuildTouch(ScrcpyConstants.ACTION_DOWN, 0, 0, 1080, 2400).Length);
        Assert.Equal(21, ControlMessages.BuildScroll(0, 0, 1080, 2400, 0f, 0f).Length);
        Assert.Equal(5, ControlMessages.BuildText(string.Empty).Length);

        Assert.Equal(14, ControlMessages.KEYCODE_MESSAGE_SIZE);
        Assert.Equal(32, ControlMessages.TOUCH_MESSAGE_SIZE);
        Assert.Equal(21, ControlMessages.SCROLL_MESSAGE_SIZE);
        Assert.Equal(5, ControlMessages.TEXT_HEADER_SIZE);
    }

    // ---------------------------------------------------------------- keycode

    [Fact]
    public void BuildKeycode_HOME按下_逐字节匹配()
    {
        byte[] actual = ControlMessages.BuildKeycode(ScrcpyConstants.ACTION_KEY_DOWN, ScrcpyConstants.KEYCODE_HOME);

        byte[] expected =
        {
            0x00,                   // type = TYPE_INJECT_KEYCODE
            0x00,                   // action = ACTION_KEY_DOWN
            0x00, 0x00, 0x00, 0x03, // keycode = KEYCODE_HOME(3)，u32 BE
            0x00, 0x00, 0x00, 0x00, // repeat = 0
            0x00, 0x00, 0x00, 0x00  // metaState = 0
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildKeycode_APP_SWITCH抬起_逐字节匹配()
    {
        byte[] actual = ControlMessages.BuildKeycode(ScrcpyConstants.ACTION_KEY_UP, ScrcpyConstants.KEYCODE_APP_SWITCH);

        byte[] expected =
        {
            0x00,
            0x01,                   // ACTION_KEY_UP
            0x00, 0x00, 0x00, 0xBB, // 187 = 0xBB
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildKeycode_repeat与metaState按大端写入()
    {
        byte[] actual = ControlMessages.BuildKeycode(
            ScrcpyConstants.ACTION_KEY_DOWN, keycode: 0x01020304, repeat: 0x0A0B0C0Du, metaState: 0x11223344u);

        byte[] expected =
        {
            0x00, 0x00,
            0x01, 0x02, 0x03, 0x04, // 大端：高位字节在前，任何小端实现都会在此失败
            0x0A, 0x0B, 0x0C, 0x0D,
            0x11, 0x22, 0x33, 0x44
        };

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ScrcpyConstants.KEYCODE_HOME, 3)]
    [InlineData(ScrcpyConstants.KEYCODE_BACK, 4)]
    [InlineData(ScrcpyConstants.KEYCODE_VOLUME_UP, 24)]
    [InlineData(ScrcpyConstants.KEYCODE_VOLUME_DOWN, 25)]
    [InlineData(ScrcpyConstants.KEYCODE_POWER, 26)]
    [InlineData(ScrcpyConstants.KEYCODE_APP_SWITCH, 187)]
    public void Android_keycode取值必须与官方一致(int actual, int expected)
    {
        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------- touch

    [Fact]
    public void BuildTouch_DOWN_逐字节匹配()
    {
        byte[] actual = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_DOWN, x: 540, y: 1200, w: 1080, h: 2400);

        byte[] expected =
        {
            0x02,                                           // type = TYPE_INJECT_TOUCH_EVENT
            0x00,                                           // action = ACTION_DOWN
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // pointerId = (u64)-1
            0x00, 0x00, 0x02, 0x1C,                         // x = 540
            0x00, 0x00, 0x04, 0xB0,                         // y = 1200
            0x04, 0x38,                                     // w = 1080 (u16)
            0x09, 0x60,                                     // h = 2400 (u16)
            0xFF, 0xFF,                                     // pressure = 1.0 定点
            0x00, 0x00, 0x00, 0x01,                         // actionButton = BUTTON_PRIMARY
            0x00, 0x00, 0x00, 0x01                          // buttons = BUTTON_PRIMARY
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildTouch_MOVE_actionButton为0且buttons保持按下()
    {
        byte[] m = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_MOVE, 1, 2, 1080, 2400);

        Assert.Equal(0x02, m[0]);
        Assert.Equal(ScrcpyConstants.ACTION_MOVE, m[1]);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, m.Skip(22).Take(2).ToArray());          // pressure = 0xFFFF
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, m.Skip(24).Take(4).ToArray()); // actionButton = 0
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, m.Skip(28).Take(4).ToArray()); // buttons = 1
    }

    [Theory]
    [InlineData(ScrcpyConstants.ACTION_UP)]
    [InlineData(ScrcpyConstants.ACTION_CANCEL)]
    public void BuildTouch_抬起与取消_压力归零且buttons归零(byte action)
    {
        byte[] m = ControlMessages.BuildTouch(action, 1, 2, 1080, 2400);

        Assert.Equal(action, m[1]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, m.Skip(22).Take(2).ToArray());             // pressure = 0
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, m.Skip(24).Take(4).ToArray()); // actionButton = 1
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, m.Skip(28).Take(4).ToArray()); // buttons = 0
    }

    [Fact]
    public void BuildTouch_pointerId恒为全FF的8字节()
    {
        byte[] m = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_DOWN, 0, 0, 1, 1);
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 8).ToArray(), m.Skip(2).Take(8).ToArray());
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, ScrcpyConstants.POINTER_ID_MOUSE);
    }

    [Fact]
    public void BuildTouch_坐标为负时按有符号大端写入()
    {
        // 坐标字段是 i32：-1 → FF FF FF FF；若误用 u32 转换会得到同样结果，
        // 故再用 -2 与 -256 校验低位，确保是补码而非绝对值。
        byte[] m = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_MOVE, -2, -256, 1080, 2400);

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFE }, m.Skip(10).Take(4).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x00 }, m.Skip(14).Take(4).ToArray());
    }

    [Fact]
    public void BuildTouch_宽高越界时收敛到u16范围()
    {
        byte[] tooBig = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_DOWN, 0, 0, 70000, 999999);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, tooBig.Skip(18).Take(2).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, tooBig.Skip(20).Take(2).ToArray());

        byte[] negative = ControlMessages.BuildTouch(ScrcpyConstants.ACTION_DOWN, 0, 0, -5, -1);
        Assert.Equal(new byte[] { 0x00, 0x00 }, negative.Skip(18).Take(2).ToArray());
        Assert.Equal(new byte[] { 0x00, 0x00 }, negative.Skip(20).Take(2).ToArray());
    }

    // ---------------------------------------------------------------- scroll

    [Fact]
    public void BuildScroll_向上滚一格_逐字节匹配()
    {
        byte[] actual = ControlMessages.BuildScroll(x: 540, y: 1200, w: 1080, h: 2400, hScroll: 0f, vScroll: 1f);

        byte[] expected =
        {
            0x03,                   // type = TYPE_INJECT_SCROLL_EVENT
            0x00, 0x00, 0x02, 0x1C, // x = 540
            0x00, 0x00, 0x04, 0xB0, // y = 1200
            0x04, 0x38,             // w = 1080
            0x09, 0x60,             // h = 2400
            0x00, 0x00,             // hScroll = 0.0
            0x7F, 0xFF,             // vScroll = 1.0 → i16fp 上界
            0x00, 0x00, 0x00, 0x01  // buttons = BUTTON_PRIMARY
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildScroll_向下滚一格_取i16fp下界()
    {
        byte[] m = ControlMessages.BuildScroll(0, 0, 1080, 2400, 0f, -1f);
        Assert.Equal(new byte[] { 0x80, 0x00 }, m.Skip(15).Take(2).ToArray());
    }

    [Fact]
    public void BuildScroll_水平与垂直分量各占其位()
    {
        byte[] m = ControlMessages.BuildScroll(0, 0, 1080, 2400, hScroll: 0.5f, vScroll: -0.5f);

        Assert.Equal(new byte[] { 0x40, 0x00 }, m.Skip(13).Take(2).ToArray());  // +0.5 → 0x4000
        Assert.Equal(new byte[] { 0xC0, 0x00 }, m.Skip(15).Take(2).ToArray());  // -0.5 → -0x4000
    }

    // ---------------------------------------------------------------- 定点数

    [Theory]
    [InlineData(0f, 0x0000)]
    [InlineData(0.5f, 0x8000)]
    [InlineData(1f, 0xFFFF)]
    [InlineData(2f, 0xFFFF)]     // 上溢 clamp
    [InlineData(-1f, 0x0000)]    // 下溢 clamp
    public void FloatToU16Fp_边界与常用值(float input, int expected)
    {
        Assert.Equal((ushort)expected, ScrcpyConstants.FloatToU16Fp(input));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 0x4000)]
    [InlineData(-0.5f, -0x4000)]
    [InlineData(1f, 0x7FFF)]
    [InlineData(-1f, -0x8000)]
    [InlineData(5f, 0x7FFF)]     // 上溢 clamp
    [InlineData(-5f, -0x8000)]   // 下溢 clamp
    public void FloatToI16Fp_边界与常用值(float input, int expected)
    {
        Assert.Equal((short)expected, ScrcpyConstants.FloatToI16Fp(input));
    }

    // ---------------------------------------------------------------- text

    [Fact]
    public void BuildText_空与null都只产出5字节头()
    {
        foreach (string? text in new[] { null, string.Empty })
        {
            byte[] m = ControlMessages.BuildText(text);
            Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00 }, m);
        }
    }

    [Fact]
    public void BuildText_ASCII_头部长度与载荷一致()
    {
        byte[] m = ControlMessages.BuildText("abc");

        Assert.Equal(8, m.Length);
        Assert.Equal(0x01, m[0]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03 }, m.Skip(1).Take(4).ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes("abc"), m.Skip(5).ToArray());
    }

    [Fact]
    public void BuildText_中文按UTF8编码而非本地代码页()
    {
        byte[] m = ControlMessages.BuildText("中");

        Assert.Equal(5 + 3, m.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03 }, m.Skip(1).Take(4).ToArray());
        Assert.Equal(new byte[] { 0xE4, 0xB8, 0xAD }, m.Skip(5).ToArray());
    }

    [Fact]
    public void BuildText_超长时按码点边界截断且不产生半个字符()
    {
        // "a"(1B) + 100 个 4 字节 emoji = 401 字节 > 300：
        // 截断点 300 落在某个 emoji 的第 4 字节上，必须回退到 297（= 1 + 4*74）。
        var sb = new StringBuilder("a");
        for (int i = 0; i < 100; i++) sb.Append("\U0001F600");

        byte[] m = ControlMessages.BuildText(sb.ToString());
        byte[] payload = m.Skip(5).ToArray();

        // 1) 声明长度必须等于实际载荷长度
        uint declared = (uint)((m[1] << 24) | (m[2] << 16) | (m[3] << 8) | m[4]);
        Assert.Equal((uint)payload.Length, declared);

        // 2) 不得超过服务端上限
        Assert.True(payload.Length <= ScrcpyConstants.TEXT_MAX_LENGTH,
                    $"载荷 {payload.Length} 字节超过上限 {ScrcpyConstants.TEXT_MAX_LENGTH}");
        Assert.Equal(297, payload.Length);

        // 3) 严格解码不得抛异常（证明没有半个码点）
        var strict = Encoding.GetEncoding("utf-8",
                                          EncoderFallback.ExceptionFallback,
                                          DecoderFallback.ExceptionFallback);
        string decoded = strict.GetString(payload);
        Assert.DoesNotContain('\uFFFD', decoded);
        Assert.Equal("a" + string.Concat(Enumerable.Repeat("\U0001F600", 74)), decoded);
    }

    [Fact]
    public void BuildText_恰好300字节时不截断()
    {
        string text = new('x', ScrcpyConstants.TEXT_MAX_LENGTH);
        byte[] m = ControlMessages.BuildText(text);

        Assert.Equal(5 + ScrcpyConstants.TEXT_MAX_LENGTH, m.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x2C }, m.Skip(1).Take(4).ToArray()); // 300 = 0x12C
    }

    // ---------------------------------------------------------------- 常量与 scid

    [Fact]
    public void 协议常量取值必须与scrcpy官方一致()
    {
        Assert.Equal(0, ScrcpyConstants.TYPE_INJECT_KEYCODE);
        Assert.Equal(1, ScrcpyConstants.TYPE_INJECT_TEXT);
        Assert.Equal(2, ScrcpyConstants.TYPE_INJECT_TOUCH_EVENT);
        Assert.Equal(3, ScrcpyConstants.TYPE_INJECT_SCROLL_EVENT);

        Assert.Equal(0, ScrcpyConstants.ACTION_DOWN);
        Assert.Equal(1, ScrcpyConstants.ACTION_UP);
        Assert.Equal(2, ScrcpyConstants.ACTION_MOVE);
        Assert.Equal(3, ScrcpyConstants.ACTION_CANCEL);

        Assert.Equal(1u, ScrcpyConstants.BUTTON_PRIMARY);
        Assert.Equal(0u, ScrcpyConstants.METASTATE_NONE);
        Assert.Equal(12, ScrcpyConstants.STREAM_HEADER_SIZE);
        Assert.Equal(64, ScrcpyConstants.DEVICE_NAME_SIZE);
        Assert.Equal(300, ScrcpyConstants.TEXT_MAX_LENGTH);
    }

    [Fact]
    public void codec_id取值等于四字符码的大端整数()
    {
        // 'h','2','6','4' → 0x68 0x32 0x36 0x34
        Assert.Equal(0x68323634u, ScrcpyConstants.CODEC_H264);
        Assert.Equal(0x68323635u, ScrcpyConstants.CODEC_H265);
        Assert.Equal(0x00617631u, ScrcpyConstants.CODEC_AV1);

        Assert.Equal("h264", ScrcpyConstants.CodecName(ScrcpyConstants.CODEC_H264));
        Assert.Equal("h265", ScrcpyConstants.CodecName(ScrcpyConstants.CODEC_H265));
        Assert.Equal("av1", ScrcpyConstants.CodecName(ScrcpyConstants.CODEC_AV1));
        Assert.Equal("0xDEADBEEF", ScrcpyConstants.CodecName(0xDEADBEEFu));
    }

    [Fact]
    public void GenerateScid_恒为8位小写十六进制()
    {
        for (int i = 0; i < 200; i++)
        {
            string scid = ScrcpyConstants.GenerateScid();

            Assert.Equal(8, scid.Length);
            Assert.All(scid, c => Assert.True(
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                $"scid 含非法字符 '{c}'：{scid}"));

            // 必须是 31 位随机数（最高位为 0），否则 server 端解析会溢出
            uint value = Convert.ToUInt32(scid, 16);
            Assert.True(value <= int.MaxValue, $"scid 超出 31 位范围：{scid}");
        }
    }
}
