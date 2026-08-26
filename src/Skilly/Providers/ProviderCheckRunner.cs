using Skilly.State;
using Skilly.Providers.SkillsCli;
using Skilly.Providers.GitHub;
using Skilly.Providers.Apm;

namespace Skilly.Providers;

public sealed record CheckRefreshResult(int CheckedCount, int FailureCount);

public sealed class ProviderCheckRunner
{
    private readonly GitHubProvider _provider;
    private readonly StateStore _stateStore;
    private readonly SkillsCliProvider? _skillsProvider;
    private readonly ApmProvider? _apmProvider;

    public ProviderCheckRunner(GitHubProvider provider, StateStore stateStore, SkillsCliProvider? skillsProvider = null, ApmProvider? apmProvider = null)
    {
        _provider = provider;
        _stateStore = stateStore;
        _skillsProvider = skillsProvider;
        _apmProvider = apmProvider;
    }

    public CheckRefreshResult Refresh()
    {
        var state = _stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Update checks are unavailable while a mutation is pending.");
        }

        var checkedCount = 0;
        var failureCount = 0;
        foreach (var record in state.Records.Where(record =>
                      string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal)
                     || (_skillsProvider is not null && string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal))
                     || (_apmProvider is not null && string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal))))
        {
            checkedCount++;
            var result = string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal)
                ? _skillsProvider!.Check(record)
                : string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal)
                    ? _apmProvider!.Check(record)
                    : _provider.Check(record);
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

        _stateStore.Save(state);
        return new CheckRefreshResult(checkedCount, failureCount);
    }
}
