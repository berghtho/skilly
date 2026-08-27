using System.IO;
using System.Text;

namespace Skilly.Infrastructure;

public sealed class RollingLog
{
    private const long MaxFileBytes = 512 * 1024;
    private const int MaxRetainedFiles = 20;

    private readonly object _gate = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private string _writerPath = string.Empty;

    public RollingLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception)
        => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                var path = CurrentFilePath();
                if (_writer is null || !string.Equals(_writerPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _writer?.Dispose();
                    _writerPath = path;
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, Encoding.UTF8)
                    {
                        AutoFlush = true,
                    };
                }

                _writer.WriteLine($"{DateTimeOffset.Now:O} [{level}] {SensitiveDataRedactor.Redact(message)}");
            }
            catch
            {
            }
        }
    }

    private string CurrentFilePath()
    {
        var baseName = $"skilly-{DateTime.Now:yyyyMMdd}";
        var candidate = Path.Combine(_directory, baseName + ".log");
        var sequence = 1;
        while (File.Exists(candidate) && new FileInfo(candidate).Length >= MaxFileBytes)
        {
            candidate = Path.Combine(_directory, $"{baseName}.{sequence}.log");
            sequence++;
        }

        return candidate;
    }

    private void RollIfNeeded()
    {
        if (_writer is null)
        {
            return;
        }

        var path = _writerPath;
        if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
        {
            _writer.Dispose();
            _writer = null;
            var rolled = FindFreeRolledPath(path);
            File.Move(path, rolled);
        }
    }

    private string FindFreeRolledPath(string originalPath)
    {
        var sequence = 1;
        string candidate;
        do
        {
            candidate = originalPath.Replace(".log", $".{sequence}.log", StringComparison.OrdinalIgnoreCase);
            sequence++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = Directory.GetFiles(_directory, "skilly-*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            foreach (var stale in files.Skip(MaxRetainedFiles))
            {
                File.Delete(stale);
            }
        }
        catch
        {
        }
    }
}
