using System;
using System.Drawing;

namespace MultiScrcpy.UI;

/// <summary>
/// 设备卡片的<b>纯几何</b>尺寸推导（无 WinForms 依赖，可无头单测）。
/// <para>
/// <b>为什么需要它</b>：<see cref="DeviceCard"/> 的画面区尺寸完全由卡片外框尺寸决定
/// （画面区 = 卡片宽 − <see cref="ChromeWidth"/> × 卡片高 − <see cref="ChromeHeight"/>）。
/// 实测：卡片 240×600 → 画面区 238×508。
/// 旧实现把卡片写死成 <c>CardBaseWidth × CardBaseHeight</c>（240×600）这一<b>固定竖长矩形</b>，
/// 设备旋转到横屏后画面区依旧是竖长的，<see cref="Protocol.CoordinateMapper.ComputeLetterbox"/>
/// 为保持设备原始比例只能把宽画面缩到很小并居中，于是上下出现大黑边、观感被「压缩」。
/// </para>
/// <para>
/// <b>修复策略</b>：让卡片尺寸跟随设备方向。
/// 竖屏（含方向未知）走<b>与旧实现逐字节一致</b>的路径，保住既有 240×600 长屏优化；
/// 横屏则把竖屏基准画面区<b>交换宽高</b>得到横长基准框，再按设备真实比例内切，
/// 使画面区自身就等于设备比例 —— letterbox 退化为满铺，黑边趋近于 0，且不改变
/// <see cref="Protocol.CoordinateMapper"/> 的保比例语义。
/// </para>
/// </summary>
public static class DeviceCardLayout
{
    /// <summary><see cref="DeviceCard"/> 的 <c>Padding</c>（单边像素）。</summary>
    public const int CardPadding = 1;

    /// <summary>
    /// 卡片宽 − 画面区宽 = 2（左右各 1px 内边距）。
    /// <para>
    /// ⭐ <b>实测值，勿凭直觉改</b>：<c>UserControl.BorderStyle = FixedSingle</c> 在 .NET 8 WinForms 下
    /// <b>只负责绘制边框，不缩减客户区</b>（BorderStyle 取 None / FixedSingle / Fixed3D 时
    /// 画面区尺寸完全相同），因此边框<b>不占</b>布局像素，只有 <c>Padding</c> 占。
    /// 早期注释与文档写的「−4 / −94」把边框也算了进去，实际是「−2 / −92」。
    /// </para>
    /// </summary>
    public const int ChromeWidth = CardPadding * 2;

    /// <summary>卡片高 − 画面区高 = 2 + 26 + 64 = 92（上下内边距 + 标题栏 + 按键区）。</summary>
    public const int ChromeHeight = CardPadding * 2 + DeviceCard.TitleHeight + DeviceCard.ButtonAreaHeight;

    /// <summary>竖屏卡片宽度下限（像素），与修复前的 <c>ApplyScale</c> 完全一致。</summary>
    public const int MinCardWidth = 160;

    /// <summary>竖屏卡片高度下限（像素），与修复前的 <c>ApplyScale</c> 完全一致。</summary>
    public const int MinCardHeight = 280;

    /// <summary>缩放比例下限（防御非法输入）。</summary>
    private const double MinScale = 0.1;

    /// <summary>缩放比例上限（防御非法输入）。</summary>
    private const double MaxScale = 8.0;

    /// <summary>
    /// 判定设备当前是否为横屏。
    /// <para>正方形与未知尺寸一律视为竖屏，走旧路径，避免边界情况引入行为变化。</para>
    /// </summary>
    /// <param name="videoW">设备视频帧宽。</param>
    /// <param name="videoH">设备视频帧高。</param>
    /// <returns>宽严格大于高且两者均为正时返回 <c>true</c>。</returns>
    public static bool IsLandscape(int videoW, int videoH)
    {
        return videoW > 0 && videoH > 0 && videoW > videoH;
    }

    /// <summary>
    /// 按设备方向与缩放比例推导卡片外框尺寸。
    /// </summary>
    /// <param name="cardBaseWidth">配置中的竖屏基准宽（<c>AppConfig.CardBaseWidth</c>）。</param>
    /// <param name="cardBaseHeight">配置中的竖屏基准高（<c>AppConfig.CardBaseHeight</c>）。</param>
    /// <param name="scale">缩放比例（0.5 / 0.75 / 1.0 / 1.5 / 2.0）。</param>
    /// <param name="videoW">设备视频帧宽；<c>&lt;= 0</c> 表示方向未知。</param>
    /// <param name="videoH">设备视频帧高；<c>&lt;= 0</c> 表示方向未知。</param>
    /// <returns>卡片外框尺寸（含边框、标题栏与按键区）。</returns>
    public static Size ComputeCardSize(int cardBaseWidth, int cardBaseHeight, double scale,
                                       int videoW, int videoH)
    {
        double s = NormalizeScale(scale);
        int baseW = Math.Max(MinCardWidth, cardBaseWidth);
        int baseH = Math.Max(MinCardHeight, cardBaseHeight);

        if (!IsLandscape(videoW, videoH))
        {
            // ⭐ 竖屏 / 未知方向：与修复前的 ApplyScale 完全等价，不得退化。
            int w = Math.Max(MinCardWidth, (int)Math.Round(baseW * s));
            int h = Math.Max(MinCardHeight, (int)Math.Round(baseH * s));
            return new Size(w, h);
        }

        // 竖屏基准下的画面区（100% 缩放时为 238x508，实测值）。
        int baseImgW = Math.Max(1, baseW - ChromeWidth);
        int baseImgH = Math.Max(1, baseH - ChromeHeight);

        // ⭐ 交换宽高得到「横长基准框」：长边用竖屏画面区的高，短边用竖屏画面区的宽，
        //    这样横屏卡片与竖屏卡片的画面面积量级一致，网格观感不跳变。
        //    下限同样由竖屏下限换算而来（160/280 → 短边 158、长边 188），
        //    并且【只作用在基准框上】，后续内切仍严格按设备比例，绝不会因为下限产生黑边。
        int boxW = Math.Max(MinCardHeight - ChromeHeight, (int)Math.Round(baseImgH * s));
        int boxH = Math.Max(MinCardWidth - ChromeWidth, (int)Math.Round(baseImgW * s));

        double fit = Math.Min((double)boxW / videoW, (double)boxH / videoH);
        int imgW = Math.Max(1, (int)Math.Round(videoW * fit));
        int imgH = Math.Max(1, (int)Math.Round(videoH * fit));

        return new Size(imgW + ChromeWidth, imgH + ChromeHeight);
    }

    /// <summary>
    /// 由卡片外框尺寸反推画面区（<see cref="ScreenView"/>）尺寸。
    /// <para>与 <c>DeviceCard</c> 的 <c>TableLayoutPanel</c> 布局结果一一对应，供单测断言使用。</para>
    /// </summary>
    /// <param name="cardSize">卡片外框尺寸。</param>
    /// <returns>画面区尺寸（至少 1x1）。</returns>
    public static Size ComputeScreenArea(Size cardSize)
    {
        return new Size(Math.Max(1, cardSize.Width - ChromeWidth),
                        Math.Max(1, cardSize.Height - ChromeHeight));
    }

    /// <summary>把缩放比例收敛到合法区间（非法值回落到 1.0）。</summary>
    private static double NormalizeScale(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            return 1.0;
        }

        if (scale < MinScale)
        {
            return MinScale;
        }

        return scale > MaxScale ? MaxScale : scale;
    }
}
