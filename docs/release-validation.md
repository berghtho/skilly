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
  -CleanProfileAttestation $env:SKILLY_CLEAN_PROFILE_ATTESTATION `
  -RunCrossHarness
```

The release command fails when any gate fails or is skipped. Use `-AllowSkippedGates` only for an explicitly partial local diagnostic run; skipped prerequisites remain recorded and are never presented as release success.

The routine packaged test is deliberately reported as `portable-runtime-proof`, not as the live clean-profile gate. It maps profile directories to isolated locations, disables system runtime lookup, verifies internal single-file hosting, observes the second-launch focus signal, and shuts down cleanly. It does not create a Windows account and cannot attest that the current account has no .NET installation.

For the actual clean-profile gate, copy the exact published `Skilly.exe` and `scripts/Test-CleanWindowsProfile.ps1` to a disposable Windows 11 x64 user profile with no `dotnet` command or system .NET roots, then run:

```powershell
.\Test-CleanWindowsProfile.ps1 -ExePath .\Skilly.exe -EvidencePath .\clean-windows-profile.json
```

Supply that JSON as `-CleanProfileAttestation` when validating the same artifact. The release script checks the schema, observations, and artifact SHA-256 before reporting `clean-windows-profile` as `PASSED`; otherwise the exact external blocker remains `SKIPPED`.

The provider live gates use provider-level install, check, Managed Reinstall, and uninstall for an externally supplied source. To additionally exercise provider-level Update deterministically, supply a provider-compatible local package directory as `SKILLY_LIVE_SKILLS_FIXTURE_TEMPLATE` or `SKILLY_LIVE_APM_FIXTURE_TEMPLATE`. The test copies the template to its temporary root, mutates only that copy, requires `Update Available`, and invokes the provider adapter's Update. Evidence records whether Update or the immutable-source Managed Reinstall path actually ran; no credential is copied or persisted by this fixture seam.

The equivalent reproducible publish command is:

```powershell
dotnet publish .\src\Skilly\Skilly.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\release\win-x64\publish
```

## Canonical scenarios

| Scenario | Deterministic evidence | Live release gate |
| --- | --- | --- |
| 1. Portable application | Packaged `Skilly.exe` proves internal single-file hosting under isolated directory mappings, disabled runtime lookup, logged focus signal, idle shutdown, LocalAppData placement, and working-directory non-interference; this is explicitly not an actual clean-profile attestation | Externally generated, exact-artifact attestation from an actual Windows 11 x64 user profile where the script verifies no `dotnet` command or system .NET roots |
| 2. `skills` provider | Fake process boundary covers Inspect, Install, Check, Update, Managed Reinstall, Uninstall, exact `skills@1.5.23` arguments, clean remove/add, lock/content/state/exposure reconciliation, false success, and rollback | Provider-level install/check/reinstall/uninstall for `SKILLY_LIVE_SKILLS_SOURCE`; provider-level Update only when a copied mutable fixture template is supplied |
| 3. Microsoft APM provider | Fake process boundary covers all five common operations plus Managed Reinstall, `apm-cli` branding/version, global canonical topology, provider-owned remove/install, evidence, no merge, and rollback | Provider-level install/check/reinstall/uninstall with supported APM; provider-level Update only when a copied mutable fixture template is supplied |
| 4. Cursor pstack source | Exact 45-Skill fixture, including nested `grokbot/make-bot-ui`, path identity, exact `poteto-mode`/`Poteto Mode` aliases, and complete-folder acquisition | Current URL at an exact recorded commit; any count other than the reconciled fixture fails closed |
| 5. Private GitHub | Authenticated API fixture, selected-folder acquisition, and credential canaries excluded from state/logs | Credential-free URL through active `gh`; verifies private visibility and selected acquisition, no `gh auth token`/`--token` process log, and no Skilly state |
| 6. Inventory classification | Observable inventory entries cover Managed, Verified Adoption Available, Unmanaged, legacy, Invalid Metadata, duplicate/Collision, unknown links, and Harness Exposures without project scanning | None |
| 7. Adoption | Exact evidence writes authority and a junction without changing bytes; drift, ambiguity, legacy roots, and unverifiable evidence remain Unmanaged | None |
| 8. Read-only checks | Selected change, unrelated commit, pin, moved/missing pin, source failure, stale prior result, and no filesystem/provider-state mutation | Provider live gates where configured |
| 9. Overwrite protection | Collision/local drift refusal, explicit GitHub/`skills`/APM Managed Reinstall plans with exact starting hash/path/revision, owning-provider replacement, no merge, rollback, and exact-path Unmanaged removal | None |
| 10. Provider/postcondition failure | Nonzero, false success, wrong payload/topology/evidence, state-commit failure, safe restore, and ambiguous Recovery Required | Provider live gates fail on incompatible output rather than weakening invariants |
| 11. Interrupted mutation | Cancellation retains journal/snapshot; restart restores without retry or enters Recovery Required | Packaged clean shutdown plus deterministic interruption injection |
| 12. State loss/incompatibility | Missing state remains Unmanaged, backup recovery, forward migration backup, corrupt state and newer schema read-only behavior | None |

## Accessibility and Harnesses

Packaged UI Automation verifies names, keyboard focus destinations, access-keyed controls, provider selection, polite status and failure diagnostics, and the source/inventory/detail/status structure. It also verifies that Managed Reinstall displays the exact path and verified revision and that cancellation preserves local content. Provider tests establish the confirmed replacement and rollback postconditions without duplicating them through UI Automation. The opt-in cross-Harness script creates one uniquely named canonical fixture plus one Claude junction, invokes it in real sessions for all four Harnesses, records versions and a content hash, and removes both fixture paths in `finally`.

The script never treats command presence alone as a live pass. Private source identity and provider source values are supplied externally; credentials are neither written to evidence nor logged by Skilly.
