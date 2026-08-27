# Release validation

Run the complete deterministic validation, publish, available Cursor live gate, and explicit prerequisite reporting from the repository root:

```powershell
.\scripts\Validate-Release.ps1
```

The ignored output is `artifacts/release/win-x64/`. It contains the self-contained `publish/Skilly.exe`, SHA-256 and size in `release-validation.json`, a readable `release-validation.md`, TRX results, and revision evidence from each live gate that ran.

Supply explicit external prerequisites only when they are real:

```powershell
.\scripts\Validate-Release.ps1 `
  -PrivateGitHubUrl $env:SKILLY_LIVE_PRIVATE_GITHUB_URL `
  -SkillsSource $env:SKILLY_LIVE_SKILLS_SOURCE `
  -ApmSource $env:SKILLY_LIVE_APM_SOURCE `
  -RunCrossHarness
```

`-RequireAllGates` returns failure when any gate is skipped. Without it, command/test failures still fail the script, while missing explicit prerequisites are recorded as `SKIPPED` rather than fabricated as passes.

The equivalent reproducible publish command is:

```powershell
dotnet publish .\src\Skilly\Skilly.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\release\win-x64\publish
```

## Canonical scenarios

| Scenario | Deterministic evidence | Live release gate |
| --- | --- | --- |
| 1. Portable application | Packaged `Skilly.exe` launch, isolated LocalAppData, runtime lookup disabled, second-launch focus signal, idle shutdown, and working-directory non-interference | Clean Windows 11 x64 profile without .NET; reported skipped when unavailable |
| 2. `skills` provider | Fake process boundary covers Inspect, Install, Check, Update, Uninstall, exact `skills@1.5.23` arguments, lock/content/state/exposure reconciliation, and false success | `SKILLY_LIVE_SKILLS_SOURCE` |
| 3. Microsoft APM provider | Fake process boundary covers all five operations, `apm-cli` branding/version, global canonical topology, provider evidence, and rollback | `SKILLY_LIVE_APM_SOURCE` with pinned 0.28.0 |
| 4. Cursor pstack source | Exact 45-Skill fixture, including nested `grokbot/make-bot-ui`, path identity, exact `poteto-mode`/`Poteto Mode` aliases, and complete-folder acquisition | Current URL at an exact recorded commit; any count other than the reconciled fixture fails closed |
| 5. Private GitHub | Authenticated API fixture, selected-folder acquisition, and credential canaries excluded from state/logs | `SKILLY_LIVE_PRIVATE_GITHUB_URL` |
| 6. Inventory classification | Observable inventory entries cover Managed, Verified Adoption Available, Unmanaged, legacy, Invalid Metadata, duplicate/Collision, unknown links, and Harness Exposures without project scanning | None |
| 7. Adoption | Exact evidence writes authority and a junction without changing bytes; drift, ambiguity, legacy roots, and unverifiable evidence remain Unmanaged | None |
| 8. Read-only checks | Selected change, unrelated commit, pin, moved/missing pin, source failure, stale prior result, and no filesystem/provider-state mutation | Provider live gates where configured |
| 9. Overwrite protection | Collision/local drift refusal, explicit Managed Reinstall replacement, no merge, exact-path Unmanaged removal | None |
| 10. Provider/postcondition failure | Nonzero, false success, wrong payload/topology/evidence, state-commit failure, safe restore, and ambiguous Recovery Required | Provider live gates fail on incompatible output rather than weakening invariants |
| 11. Interrupted mutation | Cancellation retains journal/snapshot; restart restores without retry or enters Recovery Required | Packaged clean shutdown plus deterministic interruption injection |
| 12. State loss/incompatibility | Missing state remains Unmanaged, backup recovery, forward migration backup, corrupt state and newer schema read-only behavior | None |

## Accessibility and Harnesses

Packaged UI Automation verifies names, keyboard focus destinations, access-keyed controls, provider selection, polite status announcements, and the source/inventory/detail/status structure. The opt-in cross-Harness script creates one uniquely named canonical fixture plus one Claude junction, invokes it in real sessions for all four Harnesses, records versions and a content hash, and removes both fixture paths in `finally`.

The script never treats command presence alone as a live pass. Private source identity and provider source values are supplied externally; credentials are neither written to evidence nor logged by Skilly.
