namespace MultiScrcpy.Core.Scripting;

/// <summary>OCR 文字点击的锚点位置：点击偏移基于文字包围盒的哪个点。</summary>
public enum OcrTextAnchor
{
    /// <summary>文字中心（默认）。</summary>
    Center,

    /// <summary>左边中点。</summary>
    Left,

    /// <summary>右边中点——常用于点击文字右侧的按钮（如"宝图任务"后的"参加"）。</summary>
    Right,

    /// <summary>顶边中点。</summary>
    Top,

    /// <summary>底边中点。</summary>
    Bottom
}
