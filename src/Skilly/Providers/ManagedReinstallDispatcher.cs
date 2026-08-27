using Skilly.Providers.Apm;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.State;

namespace Skilly.Providers;

public sealed class ManagedReinstallDispatcher(
    GitHubProvider github,
    SkillsCliProvider skills,
    ApmProvider apm)
{
    public ProviderResult<IManagedReinstallPlan> Plan(ManagementRecord record)
        => record.Provenance.SourceProvider switch
        {
            "skills" => Convert(skills.PlanManagedReinstall(record)),
            ApmClient.ProviderId => Convert(apm.PlanManagedReinstall(record)),
            "github" => Convert(github.PlanManagedReinstall(record)),
            _ => ProviderResult<IManagedReinstallPlan>.Unsupported(
                $"Managed Reinstall is unsupported for provider '{record.Provenance.SourceProvider}'."),
        };

    public ProviderResult<LifecycleResult> Execute(
        IManagedReinstallPlan plan,
        CancellationToken cancellationToken = default)
        => plan switch
        {
            SkillsCliManagedReinstallPlan skillsPlan => skills.ManagedReinstall(skillsPlan, cancellationToken),
            ApmManagedReinstallPlan apmPlan => apm.ManagedReinstall(apmPlan, cancellationToken),
            ManagedReinstallPlan githubPlan => github.ManagedReinstall(githubPlan, cancellationToken),
            _ => ProviderResult<LifecycleResult>.Unsupported("The Managed Reinstall plan is not owned by a supported provider."),
        };

    private static ProviderResult<IManagedReinstallPlan> Convert<TPlan>(ProviderResult<TPlan> result)
        where TPlan : IManagedReinstallPlan
        => result.Succeeded
            ? ProviderResult<IManagedReinstallPlan>.Success(result.Value!, result.Diagnostics)
            : result.Status == ProviderResultStatus.Unsupported
                ? ProviderResult<IManagedReinstallPlan>.Unsupported(result.Diagnostics)
                : ProviderResult<IManagedReinstallPlan>.Failure(result.Diagnostics);
}
