using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed record CheckRefreshResult(int CheckedCount, int FailureCount);

public sealed class GitHubCheckRunner(GitHubProvider provider, StateStore stateStore)
{
    public CheckRefreshResult Refresh()
    {
        var state = stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Update checks are unavailable while a mutation is pending.");
        }

        var checkedCount = 0;
        var failureCount = 0;
        foreach (var record in state.Records.Where(static record =>
                     string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal)))
        {
            checkedCount++;
            var result = provider.Check(record);
            if (result.Succeeded)
            {
                var check = result.Value!;
                record.LatestCheck = new CheckSnapshot
                {
                    Status = check.Status,
                    InstalledRevision = check.InstalledRevision,
                    InstalledRevisionDate = check.InstalledRevisionDate,
                    AvailableRevision = check.AvailableRevision,
                    AvailableRevisionDate = check.AvailableRevisionDate,
                    AvailablePayloadHash = check.AvailablePayloadHash,
                    AvailableContentIdentity = check.AvailableContentIdentity,
                    CheckedAt = check.CheckedAt,
                    Warning = check.Warning,
                };
                continue;
            }

            failureCount++;
            var failedAt = DateTimeOffset.Now;
            if (record.LatestCheck is null)
            {
                record.LatestCheck = new CheckSnapshot
                {
                    Status = UpdateStatus.CheckFailed,
                    InstalledRevision = record.InstalledRevision,
                    CheckedAt = failedAt,
                    Failure = result.Diagnostics,
                };
            }
            else
            {
                record.LatestCheck.CheckedAt = failedAt;
                record.LatestCheck.IsStale = true;
                record.LatestCheck.Failure = result.Diagnostics;
            }
        }

        stateStore.Save(state);
        return new CheckRefreshResult(checkedCount, failureCount);
    }
}
