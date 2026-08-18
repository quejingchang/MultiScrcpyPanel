using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MultiScrcpy.Verify;

/// <summary>
/// 用 FFmpeg 官方发行包内的 <c>ffmpeg.exe</c> 生成一段离线 H.264 Annex-B 裸流样本。
/// <para>
/// 只在验证程序里使用；主程序<b>不依赖</b> ffmpeg.exe（主程序只用 libav* 原生库）。
/// </para>
/// </summary>
internal static class SampleGenerator
{
    /// <summary>ffmpeg.exe 单次执行超时（毫秒）。</summary>
    private const int TimeoutMs = 60_000;

    /// <summary>
    /// 在常见位置搜索 <c>ffmpeg.exe</c>：
    /// 输出目录旁的 <c>ffmpeg\x64</c>、仓库内 <c>native\ffmpeg</c> 递归、最后回落 PATH。
    /// </summary>
    /// <returns>可执行文件绝对路径；找不到返回 <c>null</c>。</returns>
    public static string? LocateFFmpegExe(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        // 1) 输出目录内（若有人手动放了 ffmpeg.exe）
        string beside = Path.Combine(baseDirectory, "ffmpeg", "x64", "ffmpeg.exe");
        if (File.Exists(beside))
        {
            return Path.GetFullPath(beside);
        }

        // 2) 从输出目录向上找到仓库中的 native\ffmpeg，再递归搜索（兼容官方 zip 的多层嵌套）
        DirectoryInfo? cursor = new DirectoryInfo(baseDirectory);
        for (int depth = 0; depth < 8 && cursor != null; depth++, cursor = cursor.Parent)
        {
            string nativeDir = Path.Combine(cursor.FullName, "native", "ffmpeg");
            if (!Directory.Exists(nativeDir))
            {
                continue;
            }

            try
            {
                string[] hits = Directory.GetFiles(nativeDir, "ffmpeg.exe", SearchOption.AllDirectories);
                if (hits.Length > 0)
                {
                    Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                    return hits[0];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可读则继续向上找
            }
        }

        // 3) PATH
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0)
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 中可能存在非法路径项，跳过
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 生成 <paramref name="width"/>×<paramref name="height"/>、<paramref name="frames"/> 帧的
    /// H.264 Annex-B 裸流（testsrc 彩条，libx264，无 B 帧、单 slice、无音频）。
    /// </summary>
    /// <param name="ffmpegExe">ffmpeg.exe 路径。</param>
    /// <param name="outputPath">输出 .h264 文件路径（会覆盖）。</param>
    /// <param name="width">画面宽。</param>
    /// <param name="height">画面高。</param>
    /// <param name="frames">帧数。</param>
    /// <param name="fps">帧率。</param>
    /// <returns>ffmpeg 的合并输出（stdout + stderr），供失败时诊断。</returns>
    /// <exception cref="InvalidOperationException">ffmpeg 执行失败或产物为空。</exception>
    public static string Generate(string ffmpegExe, string outputPath,
                                  int width, int height, int frames, int fps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExe);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 首选：显式关闭 sliced-threads，保证「一帧 = 一个 slice」，最贴近 scrcpy 低延迟输出。
        string[] preferred =
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"testsrc=size={width}x{height}:rate={fps}",
            "-frames:v", frames.ToString(),
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-threads", "1",
            "-x264-params", "sliced-threads=0:slices=1",
            "-pix_fmt", "yuv420p",
            "-g", "5",
            "-bf", "0",
            "-f", "h264",
            outputPath
        };

        // 回落：去掉 -x264-params（个别精简构建可能不接受该选项）
        string[] fallback =
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"testsrc=size={width}x{height}:rate={fps}",
            "-frames:v", frames.ToString(),
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-threads", "1",
            "-pix_fmt", "yuv420p",
            "-g", "5",
            "-bf", "0",
            "-f", "h264",
            outputPath
        };

        (int code, string log) = Run(ffmpegExe, preferred);
        if (code != 0 || !HasContent(outputPath))
        {
            (int code2, string log2) = Run(ffmpegExe, fallback);
            if (code2 != 0 || !HasContent(outputPath))
            {
                throw new InvalidOperationException(
                    $"ffmpeg 生成样本失败（首选退出码 {code}，回落退出码 {code2}）。" +
                    $"{Environment.NewLine}首选输出：{log}{Environment.NewLine}回落输出：{log2}");
            }

            return log2;
        }

        return log;
    }

    /// <summary>读取 ffmpeg 版本首行，用于报告 CLI 与原生库是否同源。</summary>
    public static string ReadCliVersion(string ffmpegExe)
    {
        try
        {
            (int code, string log) = Run(ffmpegExe, new[] { "-hide_banner", "-version" });
            if (code != 0)
            {
                return "未知";
            }

            using var reader = new StringReader(log);
            return reader.ReadLine()?.Trim() ?? "未知";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return "未知";
        }
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>同步执行进程并回收 stdout/stderr。</summary>
    private static (int ExitCode, string Output) Run(string exe, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程：{exe}");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程可能刚好退出
            }

            throw new InvalidOperationException($"ffmpeg 执行超时（{TimeoutMs} ms）：{exe}");
        }

        string merged = (stdout + Environment.NewLine + stderr).Trim();
        return (process.ExitCode, merged.Length == 0 ? "(无输出)" : merged);
    }
}
