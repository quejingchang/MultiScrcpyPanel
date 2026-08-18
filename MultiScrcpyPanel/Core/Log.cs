using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MultiScrcpy.Core;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// 极简静态日志：控制台 + 滚动文件（架构文档 §9.6）。
/// <para>
/// 文件 <c>logs/panel.log</c>，单文件 10MB、最多保留 5 份；写入用 <c>lock</c> 保证线程安全。
/// 格式：<c>yyyy-MM-dd HH:mm:ss.fff [LEVEL] [tid:NN] message</c>。
/// </para>
/// 不引入 Serilog / Microsoft.Extensions.Logging，减少依赖面。
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const int MaxBackupFiles = 5;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string _logFile = string.Empty;
    private static bool _initialized;
    private static bool _fileEnabled;

    /// <summary>当前最低输出级别，低于此级别的日志被丢弃。</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>当前日志文件的绝对路径（未初始化时为空串）。</summary>
    public static string LogFilePath => _logFile;

    /// <summary>
    /// 初始化日志（可重复调用，只有首次生效）。
    /// </summary>
    /// <param name="logDirectory">日志目录，默认 <c>{BaseDirectory}\logs</c>。</param>
    /// <param name="minimumLevel">最低级别，默认 Info。</param>
    public static void Setup(string? logDirectory = null, LogLevel minimumLevel = LogLevel.Info)
    {
        lock (Gate)
        {
            if (_initialized) return;
            MinimumLevel = minimumLevel;

            try
            {
                string dir = string.IsNullOrWhiteSpace(logDirectory)
                    ? Path.Combine(AppContext.BaseDirectory, "logs")
                    : logDirectory!;
                Directory.CreateDirectory(dir);
                _logFile = Path.Combine(dir, "panel.log");
                _fileEnabled = true;
            }
            catch (Exception ex)
            {
                // 日志系统自身失败不能拖垮应用：退化为仅控制台
                _fileEnabled = false;
                Console.Error.WriteLine($"[日志] 无法初始化日志文件，仅输出到控制台：{ex.Message}");
            }

            _initialized = true;
        }

        Info("==== MultiScrcpyPanel 日志启动 ====");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);

    public static void Info(string message) => Write(LogLevel.Info, message, null);

    public static void Warn(string? message) => Write(LogLevel.Warn, message ?? string.Empty, null);

    public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private static void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < MinimumLevel) return;

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0} [{1,-5}] [tid:{2:D2}] {3}",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            level.ToString().ToUpperInvariant(),
            Environment.CurrentManagedThreadId,
            message);

        if (exception != null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Gate)
        {
            try
            {
                if (level >= LogLevel.Warn) Console.Error.WriteLine(line);
                else Console.Out.WriteLine(line);
            }
            catch
            {
                // 无控制台宿主（WinExe）时写控制台可能失败，忽略
            }

            if (!_fileEnabled || _logFile.Length == 0) return;

            try
            {
                RollIfNeeded();
                File.AppendAllText(_logFile, line + Environment.NewLine, Utf8NoBom);
            }
            catch
            {
                // 磁盘异常不得影响主流程
            }
        }
    }

    /// <summary>超过 10MB 时滚动：panel.log → panel.1.log → … → panel.5.log。</summary>
    private static void RollIfNeeded()
    {
        var info = new FileInfo(_logFile);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        string dir = Path.GetDirectoryName(_logFile) ?? AppContext.BaseDirectory;

        string Backup(int index) => Path.Combine(dir, $"panel.{index}.log");

        try
        {
            string oldest = Backup(MaxBackupFiles);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (int i = MaxBackupFiles - 1; i >= 1; i--)
            {
                string src = Backup(i);
                if (File.Exists(src)) File.Move(src, Backup(i + 1), overwrite: true);
            }

            File.Move(_logFile, Backup(1), overwrite: true);
        }
        catch
        {
            // 滚动失败就继续往当前文件追加，不抛出
        }
    }
}
