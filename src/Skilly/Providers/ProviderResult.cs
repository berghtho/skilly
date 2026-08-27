namespace Skilly.Providers;

public sealed record ProviderReadiness(bool IsReady, string Provider, string Diagnostic, string? Version = null);

public interface IManagedReinstallPlan
{
    string InstallationId { get; }
    string ExactPath { get; }
    string Revision { get; }
    string StartingPayloadHash { get; }
    IReadOnlyList<string> AffectedPaths { get; }
}

public sealed class ProviderFailure(string message) : Exception(message);

public enum ProviderResultStatus
{
    Success,
    Failure,
    Unsupported,
}

public sealed record ProviderResult<T>(
    ProviderResultStatus Status,
    T? Value,
    string Diagnostics)
{
    public bool Succeeded => Status == ProviderResultStatus.Success;

    public T ValueOrThrow()
        => Succeeded && Value is not null
            ? Value
            : throw new InvalidOperationException(Diagnostics);

    public static ProviderResult<T> Success(T value, string diagnostics)
        => new(ProviderResultStatus.Success, value, diagnostics);

    public static ProviderResult<T> Failure(string diagnostics)
        => new(ProviderResultStatus.Failure, default, diagnostics);

    public static ProviderResult<T> Unsupported(string diagnostics)
        => new(ProviderResultStatus.Unsupported, default, diagnostics);
}
