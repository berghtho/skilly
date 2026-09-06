using System.IO;
using System.Text.Json;

namespace Skilly.Infrastructure;

public sealed record OperationEntry(string RunId, DateTimeOffset StartedAt, string Operation, string Skill,
    string Path, string Status, string Detail, DateTimeOffset? FinishedAt = null);

/// <summary>Diagnostic history only; never grants management authority or schedules retries.</summary>
public sealed class OperationHistoryStore(string path)
{
    private const int MaxBytes = 4 * 1024 * 1024;
    public string? Notice { get; private set; }
    public IReadOnlyList<OperationEntry> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > MaxBytes) throw new IOException("History file exceeds 4 MB.");
            var rows = JsonSerializer.Deserialize<List<OperationEntry>>(File.ReadAllText(path)) ?? [];
            return rows.Take(1000).Select(row => row.Status is "Running" or "Pending"
                ? row with { Status = "Interrupted", Detail = "The app closed before a final result was recorded. Refresh inventory and inspect recovery status; nothing was retried." }
                : row).ToList();
        }
        catch (Exception exception)
        {
            Notice = "History could not be read: " + SensitiveDataRedactor.Redact(exception.Message);
            return [];
        }
    }

    public void Save(IEnumerable<OperationEntry> entries)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var rows = entries.Take(1000).Select(row => row with
            {
                Detail = SensitiveDataRedactor.Redact(row.Detail.Length > 4000 ? row.Detail[..4000] : row.Detail),
            }).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rows);
            while (bytes.Length > MaxBytes)
            {
                rows = rows.Take(Math.Max(0, (int)((long)rows.Count * MaxBytes / bytes.Length) - 1)).ToList();
                bytes = JsonSerializer.SerializeToUtf8Bytes(rows);
            }
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            Notice = null;
        }
        catch (Exception exception)
        {
            Notice = "History could not be saved: " + SensitiveDataRedactor.Redact(exception.Message);
        }
    }
}
