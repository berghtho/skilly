using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed class CommitResolutionCache
{
    private readonly Dictionary<(string Host, string Owner, string Repository, string TrackingRule), (ResolvedCommit? Commit, GhApiException? Failure)> _results = new();

    public ResolvedCommit Resolve(GhClient client, ProvenanceInfo provenance)
    {
        var key = (provenance.Host, provenance.Owner, provenance.Repository, provenance.TrackingRule);
        if (!_results.TryGetValue(key, out var cached))
        {
            try
            {
                cached = (client.ResolveCommit(provenance.Owner, provenance.Repository, provenance.TrackingRule), null);
            }
            catch (GhApiException exception)
            {
                cached = (null, exception);
            }

            _results[key] = cached;
        }

        return cached.Failure is null ? cached.Commit! : throw cached.Failure;
    }
}
