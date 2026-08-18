namespace MultiScrcpy;

/// <summary>
/// 画面传输质量预设（全局设置，连接 / 重连后生效）。
/// <para>
/// 把用户直觉的「画质」映射为 scrcpy server 的三个编码参数：
/// <see cref="MaxSize"/>（较长边像素）、<see cref="VideoBitRate"/>（bps）、<see cref="MaxFps"/>。
/// 纯数据 + 纯函数，不依赖任何设备 / 网络，便于单测。
/// </para>
/// </summary>
public sealed class VideoQualityPresets
{
    /// <summary>预设展示名（流畅 / 标准 / 高清）。</summary>
    public string Name { get; }

    /// <summary>server 侧最大边长（较长边）。</summary>
    public int MaxSize { get; }

    /// <summary>视频码率（bps）。</summary>
    public int VideoBitRate { get; }

    /// <summary>最大帧率。</summary>
    public int MaxFps { get; }

    public VideoQualityPresets(string name, int maxSize, int videoBitRate, int maxFps)
    {
        Name = name;
        MaxSize = maxSize;
        VideoBitRate = videoBitRate;
        MaxFps = maxFps;
    }

    /// <summary>把本预设的分辨率 / 帧率写回配置（不负责保存，由调用方 <see cref="AppConfig.Save"/>）。
    /// <para>注：码率由主界面独立的「码率」下拉框控制，此处不再改动 <see cref="AppConfig.VideoBitRate"/>，
    /// 以免预设覆盖用户的独立码率选择。</para>
    /// </summary>
    public void ApplyTo(AppConfig cfg)
    {
        cfg.MaxSize = MaxSize;
        cfg.MaxFps = MaxFps;
    }

    /// <summary>三档预设；顺序即下拉框顺序。默认（索引 0）为「流畅」，与 <see cref="AppConfig"/> 默认一致。</summary>
    public static readonly VideoQualityPresets[] Presets =
    {
        // 流畅：近 1:1 预览，最省解码 / 网络 / 设备编码（P0 默认）
        new("流畅", 480, 2_000_000, 30),
        // 标准：单设备常用，画质与开销均衡
        new("标准", 720, 4_000_000, 30),
        // 高清：少量设备看细节 / 截图，带宽与 CPU 开销明显上升
        new("高清", 1024, 8_000_000, 30),
    };

    /// <summary>根据现有配置匹配最接近的预设索引；无匹配返回 -1（表示自定义，不预选）。
    /// 仅比较分辨率 / 帧率，码率由独立控件管理。</summary>
    public static int MatchIndex(AppConfig cfg)
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            VideoQualityPresets p = Presets[i];
            if (p.MaxSize == cfg.MaxSize && p.MaxFps == cfg.MaxFps)
            {
                return i;
            }
        }

        return -1;
    }
}
