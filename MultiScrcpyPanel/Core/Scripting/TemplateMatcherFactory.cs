namespace MultiScrcpy.Core.Scripting;

/// <summary>
/// 模板匹配器工厂：直接返回 <see cref="OpenCvTemplateMatcher"/>（OpenCvSharp4，照搬 OcrViewer 的 Vision.Match）。
/// <para>
/// 2026-08-19：移除旧的纯托管 <c>ManagedTemplateMatcher</c> 回退路线（对齐"完全照搬 OcrViewer"的要求）。
/// 库不可用时 <see cref="OpenCvTemplateMatcher.Match"/> 内部会优雅返回 null，由上层决定重试/跳过。
/// </para>
/// </summary>
public static class TemplateMatcherFactory
{
    private static readonly ITemplateMatcher _default = new OpenCvTemplateMatcher();

    /// <summary>默认匹配器（OpenCvTemplateMatcher）。</summary>
    public static ITemplateMatcher Default => _default;
}
