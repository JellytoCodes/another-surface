using System.Text;

namespace AnotherSurface.Core;

public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public FileAppLogger(string logPath)
    {
        _logPath = logPath;
    }

    public Task InfoAsync(string message) => WriteAsync("INFO", message, null);

    public Task ErrorAsync(string message, Exception? exception = null)
        => WriteAsync("ERROR", message, exception);

    private async Task WriteAsync(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("O"))
            .Append(" [")
            .Append(level)
            .Append("] ")
            .AppendLine(message);

        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }

        await _writeLock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(_logPath, builder.ToString());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _writeLock.Dispose();
    }
}
