using System.IO;

namespace ExHyperV.Services.Logging;

public static class AppLog
{
    private static readonly object Sync = new();
    private static RollingFileLogger? _logger;

    public static bool IsAvailable { get; private set; }
    public static string? UnavailableReason { get; private set; }
    public static string LogDirectory { get; private set; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public static event Action<string>? BecameUnavailable;

    public static void Initialize(string? baseDirectory = null)
    {
        lock (Sync)
        {
            _logger?.Dispose();
            _logger = null;
            IsAvailable = false;
            UnavailableReason = null;
            LogDirectory = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "logs");
            try
            {
                _logger = new RollingFileLogger(new RollingFileLoggerOptions { LogDirectory = LogDirectory });
                IsAvailable = true;
                WriteCore(AppLogLevel.Information, "应用", "日志服务已启动。", null, null);
            }
            catch (Exception ex)
            {
                _logger?.Dispose();
                _logger = null;
                UnavailableReason = $"无法写入日志目录“{LogDirectory}”：{SensitiveDataRedactor.Redact(ex.Message)}";
                NotifyUnavailable(UnavailableReason);
            }
        }
    }

    public static void Debug(string component, string message, AppLogContext? context = null) => Write(AppLogLevel.Debug, component, message, context);
    public static void Information(string component, string message, AppLogContext? context = null) => Write(AppLogLevel.Information, component, message, context);
    public static void Warning(string component, string message, AppLogContext? context = null, Exception? exception = null) => Write(AppLogLevel.Warning, component, message, context, exception);
    public static void Error(string component, string message, AppLogContext? context = null, Exception? exception = null) => Write(AppLogLevel.Error, component, message, context, exception);

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_logger is not null)
            {
                try { WriteCore(AppLogLevel.Information, "应用", "日志服务已停止。", null, null); } catch { }
                _logger.Dispose();
                _logger = null;
            }
            IsAvailable = false;
        }
    }

    private static void Write(AppLogLevel level, string component, string message, AppLogContext? context = null, Exception? exception = null)
    {
        lock (Sync)
        {
            if (_logger is null) return;
            try { WriteCore(level, component, message, context, exception); }
            catch (Exception ex)
            {
                IsAvailable = false;
                UnavailableReason = $"日志写入失败：{SensitiveDataRedactor.Redact(ex.Message)}";
                _logger.Dispose();
                _logger = null;
                NotifyUnavailable(UnavailableReason);
            }
        }
    }

    private static void WriteCore(AppLogLevel level, string component, string message, AppLogContext? context, Exception? exception) =>
        _logger?.Write(level, component, message, context, exception);

    private static void NotifyUnavailable(string reason)
    {
        try { BecameUnavailable?.Invoke(reason); }
        catch { }
    }
}
