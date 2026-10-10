using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dsh.Runtime.Logging;

/** 逐行落盘的文本日志:按天分文件,单文件超限后切分 .1/.2…,启动时清理过期文件。 */
public sealed class FileLogProvider : ILoggerProvider
{
    private const string FilePrefix = "dsh-";
    private const string SearchPattern = "dsh-*.log";
    private const int DateLength = 8;

    private readonly string _directory;
    private readonly LoggingOptions _options;
    private readonly Lock _sync = new();
    private StreamWriter? _writer;
    private DateOnly _date;
    private int _index;
    private bool _disposed;
    private bool _fileDisabled;

    public FileLogProvider(string directory, LoggingOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _options = options ?? new LoggingOptions();
        Directory.CreateDirectory(_directory);
        CleanExpired();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string categoryName, string text)
    {
        var timestamp = DateTimeOffset.Now;
        lock (_sync)
        {
            if (_disposed || _fileDisabled)
                return;
            try
            {
                var date = DateOnly.FromDateTime(timestamp.LocalDateTime);
                if (_writer is null || date != _date)
                    Open(date, 0);
                else if (_writer.BaseStream.Length >= _options.FileMaxBytes)
                    Open(_date, _index + 1);

                _writer!.WriteLine("{0:yyyy-MM-dd HH:mm:ss.fff zzz} [{1}] {2}: {3}",
                    timestamp, LogLevels.ToLabel(level), categoryName, Redaction.Apply(text));
                _writer.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 落盘失败不能把进程带走(同一 home 多开、磁盘满、文件被杀毒软件锁住都可能发生); 之后只留内存日志。
                _fileDisabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private void Open(DateOnly date, int startIndex)
    {
        _writer?.Dispose();
        var index = startIndex;
        while (true)
        {
            var path = PathFor(date, index);
            if (!File.Exists(path) || new FileInfo(path).Length < _options.FileMaxBytes)
                break;
            index++;
        }
        _date = date;
        _index = index;
        _writer = OpenWriter(PathFor(date, index)) ?? OpenWriter(PidPathFor(date)) ?? throw new IOException("log file is not writable");
    }

    /** 同一天里多个 dsharp 进程共用一个 home 是常态: 允许其它进程同时追加, 写不动再退到带 pid 的独立文件。 */
    private static StreamWriter? OpenWriter(string path)
    {
        try
        {
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string PidPathFor(DateOnly date)
    {
        var stamp = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Path.Combine(_directory, $"{FilePrefix}{stamp}-{Environment.ProcessId}.log");
    }

    private string PathFor(DateOnly date, int index)
    {
        var stamp = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return index == 0
            ? Path.Combine(_directory, $"{FilePrefix}{stamp}.log")
            : Path.Combine(_directory, $"{FilePrefix}{stamp}.{index}.log");
    }

    private void CleanExpired()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-_options.KeepDays);
        foreach (var path in Directory.EnumerateFiles(_directory, SearchPattern))
        {
            if (!TryParseDate(Path.GetFileNameWithoutExtension(path), out var date) || date >= cutoff)
                continue;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TryParseDate(string fileName, out DateOnly date)
    {
        date = default;
        if (!fileName.StartsWith(FilePrefix, StringComparison.Ordinal))
            return false;
        var span = fileName.AsSpan(FilePrefix.Length);
        if (span.Length < DateLength)
            return false;
        var tail = span[DateLength..];
        if (tail.Length > 0 && !IsKnownSuffix(tail))
            return false;
        return DateOnly.TryParseExact(span[..DateLength], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /** 允许的后缀: 空、`.序号`、`-进程号`。 */
    private static bool IsKnownSuffix(ReadOnlySpan<char> tail)
    {
        if (tail.Length < 2 || (tail[0] != '.' && tail[0] != '-'))
            return false;
        return int.TryParse(tail[1..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private sealed class FileLogger(FileLogProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Write(logLevel, categoryName, formatter(state, exception));
        }
    }
}
