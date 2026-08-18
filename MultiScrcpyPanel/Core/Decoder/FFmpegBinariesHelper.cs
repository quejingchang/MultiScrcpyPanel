using System;
using System.IO;
using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

namespace MultiScrcpy.Core.Decoder;

/// <summary>
/// FFmpeg 原生库路径注册与日志桥接（架构文档 §6.1）。
/// <para>
/// <b>必须在 <c>Main</c> 最早期、任何 <c>ffmpeg.*</c> 调用之前执行。</b>
/// 失败时抛 <see cref="DecoderException"/>，由 <c>Program.Main</c> 用 MessageBox 提示后退出
/// （全项目唯一允许的模态弹窗场景）。
/// </para>
/// <para>
/// 版本配对铁律：<c>FFmpeg.AutoGen 6.0.0.2</c> ↔ FFmpeg <b>6.x</b> shared 原生库
/// （<c>avutil-58</c> / <c>avcodec-60</c> / <c>swscale-7</c> / <c>swresample-4</c>）。
/// 错配的表现是运行时静默崩溃或函数签名错位，<b>不得擅自升级 NuGet 版本</b>。
/// </para>
/// </summary>
public static unsafe class FFmpegBinariesHelper
{
    /// <summary>必须存在的探针 DLL（FFmpeg 6.x）。</summary>
    private static readonly string[] RequiredLibraries =
    {
        "avutil-58.dll",
        "avcodec-60.dll",
        "swscale-7.dll",
        "swresample-4.dll"
    };

    // ⚠️ 必须用静态字段持有委托，否则会被 GC 回收 → 原生回调时崩溃
    private static av_log_set_callback_callback? _logCallback;

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>是否已成功注册。</summary>
    public static bool IsRegistered
    {
        get { lock (Gate) return _registered; }
    }

    /// <summary>已注册的原生库目录。</summary>
    public static string NativeDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// 注册原生库搜索路径并挂接 av_log 回调（幂等）。
    /// </summary>
    /// <param name="overridePath">显式目录；为空则用 <c>{BaseDirectory}\ffmpeg\x64</c>。</param>
    /// <exception cref="DecoderException">目录不存在、DLL 缺失或加载失败。</exception>
    public static void Register(string? overridePath = null)
    {
        lock (Gate)
        {
            if (_registered) return;

            string dir = string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(AppContext.BaseDirectory, "ffmpeg", "x64")
                : Path.GetFullPath(overridePath!);

            if (!Directory.Exists(dir))
            {
                throw new DecoderException(BuildMissingMessage(dir, "目录不存在"));
            }

            foreach (string lib in RequiredLibraries)
            {
                if (!File.Exists(Path.Combine(dir, lib)))
                {
                    throw new DecoderException(BuildMissingMessage(dir, $"缺少 {lib}"));
                }
            }

            try
            {
                ffmpeg.RootPath = dir;
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

                _logCallback = (p0, level, format, vl) =>
                {
                    try
                    {
                        if (level > ffmpeg.av_log_get_level()) return;

                        byte* buffer = stackalloc byte[1024];
                        int printPrefix = 1;
                        ffmpeg.av_log_format_line(p0, level, format, vl, buffer, 1024, &printPrefix);
                        string? text = Marshal.PtrToStringAnsi((IntPtr)buffer);
                        if (!string.IsNullOrWhiteSpace(text)) Log.Warn("[ffmpeg] " + text!.TrimEnd());
                    }
                    catch
                    {
                        // 原生回调内绝不允许异常逃逸
                    }
                };
                ffmpeg.av_log_set_callback(_logCallback);

                string version = ffmpeg.av_version_info();
                NativeDirectory = dir;
                _registered = true;
                Log.Info($"FFmpeg 已加载：{version}（路径 {dir}）");
            }
            catch (DllNotFoundException ex)
            {
                throw new DecoderException(BuildMissingMessage(dir, $"DLL 加载失败：{ex.Message}"), ex);
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new DecoderException(
                    $"FFmpeg 原生库版本不匹配（路径 {dir}）：{ex.Message}{Environment.NewLine}" +
                    "本项目锁定 FFmpeg.AutoGen 6.0.0.2 ↔ FFmpeg 6.x shared，请勿混用其他主版本。", ex);
            }
            catch (BadImageFormatException ex)
            {
                throw new DecoderException(
                    $"FFmpeg 原生库位数不匹配（需要 64 位，路径 {dir}）：{ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new DecoderException($"注册 FFmpeg 原生库失败（路径 {dir}）：{ex.Message}", ex);
            }
        }
    }

    /// <summary>返回 <c>av_version_info()</c>；未注册时返回占位文案。</summary>
    public static string VersionInfo()
    {
        lock (Gate)
        {
            if (!_registered) return "未加载";
        }

        try
        {
            return ffmpeg.av_version_info();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 FFmpeg 版本失败：{ex.Message}");
            return "未知";
        }
    }

    private static string BuildMissingMessage(string dir, string reason)
    {
        return
            $"未找到可用的 FFmpeg 原生库（{reason}）：{dir}{Environment.NewLine}{Environment.NewLine}" +
            $"修复步骤：{Environment.NewLine}" +
            $"  1. 在 csharp\\ 目录执行：pwsh tools\\fetch_ffmpeg.ps1{Environment.NewLine}" +
            $"  2. 确认 csharp\\native\\ffmpeg\\x64\\ 下存在 " +
            $"{string.Join("、", RequiredLibraries)}{Environment.NewLine}" +
            $"  3. 重新构建（这些 DLL 会被复制到输出目录的 ffmpeg\\x64\\）{Environment.NewLine}{Environment.NewLine}" +
            "注意：必须是 FFmpeg 6.x 的 shared（非 static）win64 构建，版本号后缀必须完全一致。";
    }
}
