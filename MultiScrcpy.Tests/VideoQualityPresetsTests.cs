using MultiScrcpy;

using Xunit;

namespace MultiScrcpy.Tests;

public class VideoQualityPresetsTests
{
    [Fact]
    public void Presets_三档齐全且顺序为流畅_标准_高清()
    {
        Assert.Equal(3, VideoQualityPresets.Presets.Length);
        Assert.Equal("流畅", VideoQualityPresets.Presets[0].Name);
        Assert.Equal("标准", VideoQualityPresets.Presets[1].Name);
        Assert.Equal("高清", VideoQualityPresets.Presets[2].Name);
    }

    [Fact]
    public void Presets_分辨率帧率语义正确()
    {
        // 画质预设只管分辨率 / 帧率；码率由主界面独立「码率」下拉框控制（见 MainForm）。
        VideoQualityPresets fluent = VideoQualityPresets.Presets[0];

        // 分辨率随档位递增、帧率合法（默认已改为 2400 自定义，不与任何预设相等）
        Assert.True(VideoQualityPresets.Presets[0].MaxSize < VideoQualityPresets.Presets[1].MaxSize);
        Assert.True(VideoQualityPresets.Presets[1].MaxSize < VideoQualityPresets.Presets[2].MaxSize);
        Assert.Equal(30, fluent.MaxFps);
    }

    [Fact]
    public void ApplyTo_只写回分辨率与帧率()
    {
        var cfg = new AppConfig();
        VideoQualityPresets high = VideoQualityPresets.Presets[2];

        high.ApplyTo(cfg);

        Assert.Equal(high.MaxSize, cfg.MaxSize);
        Assert.Equal(high.MaxFps, cfg.MaxFps);
        // 码率不受预设影响：保持调用前的值（默认 8Mbps）
        Assert.Equal(8_000_000, cfg.VideoBitRate);
    }

    [Fact]
    public void MatchIndex_匹配已有配置返回对应索引()
    {
        var cfg = new AppConfig { MaxSize = 720, VideoBitRate = 4_000_000, MaxFps = 30 };
        Assert.Equal(1, VideoQualityPresets.MatchIndex(cfg));

        var standard = new AppConfig();
        VideoQualityPresets.Presets[1].ApplyTo(standard);
        Assert.Equal(1, VideoQualityPresets.MatchIndex(standard));
    }

    [Fact]
    public void MatchIndex_无匹配返回负一表示自定义()
    {
        var cfg = new AppConfig { MaxSize = 999, VideoBitRate = 123, MaxFps = 7 };
        Assert.Equal(-1, VideoQualityPresets.MatchIndex(cfg));
    }
}
