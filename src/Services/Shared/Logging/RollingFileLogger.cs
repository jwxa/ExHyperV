using System.Globalization;
using System.IO;
using System.Text;

namespace ExHyperV.Services.Logging;

public sealed class RollingFileLogger : IDisposable
{
    public const string CurrentFileName = "ExHyperV.log";
    public const string PreviousFileName = "ExHyperV.1.log";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _sync = new();
    private readonly RollingFileLoggerOptions _options;
    private bool _disposed;

    public RollingFileLogger(RollingFileLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.LogDirectory))
            throw new ArgumentException("日志目录不能为空。", nameof(options));
        if (options.MaxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "日志文件上限必须大于 0。");

        _options = options;
        CurrentFilePath = Path.Combine(options.LogDirectory, CurrentFileName);
        PreviousFilePath = Path.Combine(options.LogDirectory, PreviousFileName);

        Directory.CreateDirectory(options.LogDirectory);
        NormalizeExistingFiles();
    }

    public string CurrentFilePath { get; }
    public string PreviousFilePath { get; }

    public void Write(
        AppLogLevel level,
        string component,
        string message,
        AppLogContext? context = null,
        Exception? exception = null) =>
        Write(AppLogEntry.Create(_options.Clock(), level, component, message, context, exception));

    public void Write(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] bytes = BuildEntry(entry);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RotateIfNeeded(bytes.LongLength);

            using var stream = new FileStream(
                CurrentFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
    }

    private byte[] BuildEntry(AppLogEntry entry)
    {
        string prefix = FormatPrefix(entry);
        string line = FormatEntry(entry) + Environment.NewLine;
        byte[] bytes = Utf8WithoutBom.GetBytes(line);
        if (bytes.LongLength <= _options.MaxFileBytes) return bytes;

        const string marker = " [日志条目过大，已截断]";
        string fixedText = prefix + marker + Environment.NewLine;
        int fixedBytes = Utf8WithoutBom.GetByteCount(fixedText);
        if (fixedBytes <= _options.MaxFileBytes)
        {
            long availableMessageBytes = _options.MaxFileBytes - fixedBytes;
            return Utf8WithoutBom.GetBytes(
                prefix + TruncateToUtf8ByteCount(entry.Message, availableMessageBytes) + marker + Environment.NewLine);
        }

        const string fallback = "[日志条目无法写入：文件上限过小]";
        return Utf8WithoutBom.GetBytes(TruncateToUtf8ByteCount(fallback, _options.MaxFileBytes));
    }

    private static string TruncateToUtf8ByteCount(string value, long maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Utf8WithoutBom.GetByteCount(value) <= maxBytes) return value;

        int length = 0;
        long bytes = 0;
        while (length < value.Length)
        {
            int characterLength = char.IsHighSurrogate(value[length])
                && length + 1 < value.Length
                && char.IsLowSurrogate(value[length + 1])
                    ? 2
                    : 1;
            int characterBytes = Utf8WithoutBom.GetByteCount(value.AsSpan(length, characterLength));
            if (bytes + characterBytes > maxBytes) break;
            bytes += characterBytes;
            length += characterLength;
        }

        return value[..length];
    }

    internal static string FormatEntry(AppLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return FormatPrefix(entry)
            + entry.Message
            + FormatProperties(entry.Properties)
            + FormatException(entry.ExceptionText);
    }

    private static string FormatPrefix(AppLogEntry entry)
    {
        var builder = new StringBuilder(128);
        builder.Append('[')
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append("] [")
            .Append(ToChineseLevel(entry.Level))
            .Append("] [")
            .Append(string.IsNullOrWhiteSpace(entry.Source) ? "应用" : entry.Source)
            .Append("] [错误分类=")
            .Append(entry.ErrorCategory)
            .Append(']');

        if (!string.IsNullOrWhiteSpace(entry.Host))
            builder.Append(" [宿主=").Append(entry.Host).Append(']');
        if (entry.SessionGeneration is long generation)
            builder.Append(" [会话=").Append(generation.ToString(CultureInfo.InvariantCulture)).Append(']');
        builder.Append(' ');
        return builder.ToString();
    }

    private static string FormatProperties(IReadOnlyList<AppLogProperty> properties)
    {
        if (properties.Count == 0) return string.Empty;

        var builder = new StringBuilder(" | ");
        bool first = true;
        foreach (AppLogProperty property in properties)
        {
            if (!first) builder.Append(' ');
            first = false;
            builder.Append(property.Name).Append('=').Append(property.Value);
        }
        return builder.ToString();
    }

    private static string FormatException(string exceptionText) => string.IsNullOrEmpty(exceptionText)
        ? string.Empty
        : $" | 异常={exceptionText}";

    private static string ToChineseLevel(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => "调试",
        AppLogLevel.Information => "信息",
        AppLogLevel.Warning => "警告",
        AppLogLevel.Error => "错误",
        _ => "信息"
    };

    private void RotateIfNeeded(long incomingBytes)
    {
        long currentLength = File.Exists(CurrentFilePath) ? new FileInfo(CurrentFilePath).Length : 0;
        if (currentLength == 0 || currentLength + incomingBytes <= _options.MaxFileBytes) return;

        File.Move(CurrentFilePath, PreviousFilePath, overwrite: true);
        TrimToLimit(PreviousFilePath);
    }

    private void NormalizeExistingFiles()
    {
        TrimToLimit(PreviousFilePath);
        if (!File.Exists(CurrentFilePath)) return;
        if (new FileInfo(CurrentFilePath).Length <= _options.MaxFileBytes) return;

        File.Move(CurrentFilePath, PreviousFilePath, overwrite: true);
        TrimToLimit(PreviousFilePath);
    }

    private void TrimToLimit(string path)
    {
        if (!File.Exists(path)) return;
        var file = new FileInfo(path);
        if (file.Length <= _options.MaxFileBytes) return;

        string temporaryPath = path + ".trim";
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long start = source.Length - _options.MaxFileBytes;
                source.Seek(start, SeekOrigin.Begin);
                SkipUtf8ContinuationBytes(source);
                source.CopyTo(target);
                target.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void SkipUtf8ContinuationBytes(Stream stream)
    {
        while (stream.Position < stream.Length)
        {
            int next = stream.ReadByte();
            if (next < 0) return;
            if ((next & 0b1100_0000) == 0b1000_0000) continue;

            stream.Seek(-1, SeekOrigin.Current);
            return;
        }
    }
}
