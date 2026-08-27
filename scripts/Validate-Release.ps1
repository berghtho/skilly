[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$PrivateGitHubUrl = $env:SKILLY_LIVE_PRIVATE_GITHUB_URL,
    [string]$SkillsSource = $env:SKILLY_LIVE_SKILLS_SOURCE,
    [string]$ApmSource = $env:SKILLY_LIVE_APM_SOURCE,
    [string]$SkillsFixtureTemplate = $env:SKILLY_LIVE_SKILLS_FIXTURE_TEMPLATE,
    [string]$ApmFixtureTemplate = $env:SKILLY_LIVE_APM_FIXTURE_TEMPLATE,
    [string]$CleanProfileAttestation = $env:SKILLY_CLEAN_PROFILE_ATTESTATION,
    [switch]$RunCrossHarness,
    [switch]$AllowSkippedGates
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo "artifacts\release\win-x64"
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repo $OutputDirectory
}
$evidenceDirectory = Join-Path $OutputDirectory "evidence"
$testResultsDirectory = Join-Path $OutputDirectory "test-results"
$publishDirectory = Join-Path $OutputDirectory "publish"
if (Test-Path -LiteralPath $evidenceDirectory) { Remove-Item -LiteralPath $evidenceDirectory -Recurse -Force }
if (Test-Path -LiteralPath $testResultsDirectory) { Remove-Item -LiteralPath $testResultsDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $testResultsDirectory -Force | Out-Null
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$results = New-Object Collections.Generic.List[object]
function Add-Result([string]$Gate, [string]$Status, [string]$Detail, [string]$Evidence = "") {
    $results.Add([pscustomobject]@{ gate = $Gate; status = $Status; detail = $Detail; evidence = $Evidence })
}

function Invoke-CommandGate([string]$Gate, [scriptblock]$Command, [string]$SuccessDetail) {
    try {
        & $Command | Out-Host
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            Add-Result $Gate "FAILED" "Command exited with code $exitCode."
            return $false
        }
        Add-Result $Gate "PASSED" $SuccessDetail
        return $true
    } catch {
        Add-Result $Gate "FAILED" $_.Exception.Message
        return $false
    }
}

function Invoke-LiveTest([string]$Gate, [string]$FullyQualifiedName, [hashtable]$Environment) {
    $saved = @{}
    try {
        foreach ($key in $Environment.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key])
        }
        $saved["SKILLY_LIVE_EVIDENCE_DIRECTORY"] = [Environment]::GetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY")
        [Environment]::SetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY", $evidenceDirectory)
        & dotnet test (Join-Path $repo "Skilly.slnx") -c Release --no-restore --no-build --filter "FullyQualifiedName=$FullyQualifiedName" --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) {
            Add-Result $Gate "FAILED" "Live test failed with exit code $LASTEXITCODE."
            return
        }
        $evidence = Join-Path $evidenceDirectory ($Gate + ".json")
        if (-not (Test-Path -LiteralPath $evidence)) {
            Add-Result $Gate "FAILED" "Live test passed but did not write revision evidence."
            return
        }
        $payload = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
        $hasMutation = $payload.evidence.PSObject.Properties.Name -contains "mutation"
        $detail = if ($hasMutation -and $payload.evidence.mutation) {
            "Live provider-level install/check/$($payload.evidence.mutation)/uninstall postconditions passed; exact observed facts are recorded."
        } else {
            "Live test postconditions passed; exact observed facts are recorded."
        }
        Add-Result $Gate "PASSED" $detail $evidence
    } catch {
        Add-Result $Gate "FAILED" $_.Exception.Message
    } finally {
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}

Push-Location $repo
try {
    $head = (& git rev-parse HEAD).Trim()
    $branch = (& git branch --show-current).Trim()
    $initialWorktree = @(& git status --porcelain=v1 --untracked-files=all | Where-Object { $_ -notmatch '^\?\? \.claude/' })
    $started = [DateTimeOffset]::UtcNow
    if ($initialWorktree.Count -eq 0) {
        Add-Result "source-cleanliness" "PASSED" "The release started from an exact committed source tree."
    } else {
        Add-Result "source-cleanliness" "FAILED" "Release validation requires a clean source tree: $($initialWorktree -join '; ')."
    }

    $buildPassed = Invoke-CommandGate "release-build" {
        & dotnet build "Skilly.slnx" -c Release
    } "Release build completed without errors."

    $exe = Join-Path $publishDirectory "Skilly.exe"
    if ($buildPassed) {
        & dotnet publish "src\Skilly\Skilly.csproj" -c Release -r win-x64 --self-contained true --no-restore -o $publishDirectory
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $exe)) {
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
            $size = (Get-Item -LiteralPath $exe).Length
            Add-Result "self-contained-publish" "PASSED" "Published win-x64 single-file Skilly.exe ($size bytes, SHA-256 $hash)." $exe
        } else {
            Add-Result "self-contained-publish" "FAILED" "Self-contained publish failed or Skilly.exe was absent."
        }
    }

    if ($buildPassed -and (Test-Path -LiteralPath $exe)) {
        $trx = Join-Path $testResultsDirectory "deterministic.trx"
        $savedEvidenceDirectory = [Environment]::GetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY")
        $savedPackagedExe = [Environment]::GetEnvironmentVariable("SKILLY_PACKAGED_EXE")
        try {
            [Environment]::SetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY", $evidenceDirectory)
            [Environment]::SetEnvironmentVariable("SKILLY_PACKAGED_EXE", $exe)
            & dotnet test "Skilly.slnx" -c Release --no-restore --no-build `
                --filter "Category!=LiveGitHubPreRelease&Category!=LiveSkillsCliPreRelease&Category!=LiveApmPreRelease" `
                --logger "trx;LogFileName=$trx" --results-directory $testResultsDirectory
        } finally {
            [Environment]::SetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY", $savedEvidenceDirectory)
            [Environment]::SetEnvironmentVariable("SKILLY_PACKAGED_EXE", $savedPackagedExe)
        }
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $trx)) {
            [xml]$testRun = Get-Content -LiteralPath $trx -Raw
            $counters = $testRun.TestRun.ResultSummary.Counters
            Add-Result "deterministic-suite" "PASSED" "$($counters.passed) passed, $($counters.failed) failed, $($counters.notExecuted) not executed." $trx
        } else {
            Add-Result "deterministic-suite" "FAILED" "Deterministic tests failed with exit code $LASTEXITCODE." $(if (Test-Path -LiteralPath $trx) { $trx } else { "" })
        }
    } else {
        Add-Result "deterministic-suite" "FAILED" "Not run because the Release build or self-contained publish failed."
    }

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        & gh auth status *> $null
        if ($LASTEXITCODE -eq 0) {
            $cursorRevision = (& gh api "repos/cursor/plugins/commits/main" --jq .sha).Trim()
            if ($LASTEXITCODE -eq 0 -and $cursorRevision -match '^[0-9a-f]{40}$') {
                Invoke-LiveTest "cursor-pstack" `
                    "Skilly.App.Tests.LiveGitHubPreReleaseTests.Current_Cursor_pstack_source_is_complete_at_one_immutable_revision" `
                    @{ SKILLY_RUN_LIVE_GITHUB_TESTS = "1"; SKILLY_EXPECTED_CURSOR_REVISION = $cursorRevision }
            } else {
                Add-Result "cursor-pstack" "FAILED" "Could not resolve Cursor main to an immutable revision."
            }
        } else {
            Add-Result "cursor-pstack" "SKIPPED" "Prerequisite missing: gh is not authenticated."
        }
    } else {
        Add-Result "cursor-pstack" "SKIPPED" "Prerequisite missing: gh executable."
    }

    if ([string]::IsNullOrWhiteSpace($PrivateGitHubUrl)) {
        Add-Result "private-github" "SKIPPED" "Prerequisite missing: SKILLY_LIVE_PRIVATE_GITHUB_URL was not supplied."
    } else {
        Invoke-LiveTest "private-github" `
            "Skilly.App.Tests.LiveGitHubPreReleaseTests.Authenticated_private_source_supports_discovery_and_selected_folder_acquisition" `
            @{ SKILLY_RUN_LIVE_GITHUB_TESTS = "1"; SKILLY_LIVE_PRIVATE_GITHUB_URL = $PrivateGitHubUrl }
    }

    if ([string]::IsNullOrWhiteSpace($SkillsSource) -and [string]::IsNullOrWhiteSpace($SkillsFixtureTemplate)) {
        Add-Result "skills-provider" "SKIPPED" "Prerequisite missing: supply SKILLY_LIVE_SKILLS_SOURCE or a provider-compatible SKILLY_LIVE_SKILLS_FIXTURE_TEMPLATE."
    } else {
        Invoke-LiveTest "skills-provider" `
            "Skilly.App.Tests.LiveSkillsCliPreReleaseTests.Pinned_provider_supports_inspect_install_read_only_check_and_uninstall_in_an_isolated_home" `
            @{ SKILLY_RUN_LIVE_SKILLS_TESTS = "1"; SKILLY_LIVE_SKILLS_SOURCE = $SkillsSource; SKILLY_LIVE_SKILLS_FIXTURE_TEMPLATE = $SkillsFixtureTemplate }
    }

    if ([string]::IsNullOrWhiteSpace($ApmSource) -and [string]::IsNullOrWhiteSpace($ApmFixtureTemplate)) {
        Add-Result "apm-provider" "SKIPPED" "Prerequisite missing: supply SKILLY_LIVE_APM_SOURCE or a provider-compatible SKILLY_LIVE_APM_FIXTURE_TEMPLATE."
    } else {
        Invoke-LiveTest "apm-provider" `
            "Skilly.App.Tests.LiveApmPreReleaseTests.Pinned_Microsoft_APM_supports_the_adapter_contract_in_an_isolated_home" `
            @{ SKILLY_RUN_LIVE_APM_TESTS = "1"; SKILLY_LIVE_APM_SOURCE = $ApmSource; SKILLY_LIVE_APM_FIXTURE_TEMPLATE = $ApmFixtureTemplate }
    }

    if ($RunCrossHarness) {
        $crossEvidence = Join-Path $evidenceDirectory "cross-harness.json"
        try {
            & (Join-Path $PSScriptRoot "Test-CrossHarness.ps1") -EvidencePath $crossEvidence
            if (Test-Path -LiteralPath $crossEvidence) {
                Add-Result "cross-harness" "PASSED" "One canonical fixture was invoked in real OpenCode, Codex, Claude Code, and GitHub Copilot sessions." $crossEvidence
            } else {
                Add-Result "cross-harness" "FAILED" "Cross-Harness sessions completed without revision evidence."
            }
        } catch {
            Add-Result "cross-harness" "FAILED" $_.Exception.Message
        }
    } else {
        Add-Result "cross-harness" "SKIPPED" "Explicit prerequisite missing: pass -RunCrossHarness after confirming all four Harness sessions are authenticated and may consume AI credits."
    }

    $portableProof = Join-Path $evidenceDirectory "portable-runtime-proof.json"
    if (Test-Path -LiteralPath $portableProof) {
        Add-Result "portable-runtime-proof" "PASSED" "Deterministic equivalent verified single-file internal hosting, disabled runtime lookup, isolated directory mapping, second-activation signal, and shutdown. It is not a live clean-profile attestation." $portableProof
    } else {
        Add-Result "portable-runtime-proof" "FAILED" "The packaged deterministic runtime test did not write its explicitly limited evidence."
    }

    $cleanProfileEvidence = Join-Path $evidenceDirectory "clean-windows-profile.json"
    if ([string]::IsNullOrWhiteSpace($CleanProfileAttestation)) {
        Add-Result "clean-windows-profile" "SKIPPED" "No actual clean Windows 11 x64 user profile without .NET is available in this run. Run scripts/Test-CleanWindowsProfile.ps1 there and supply SKILLY_CLEAN_PROFILE_ATTESTATION; the deterministic equivalent above is not substituted."
    } else {
        try {
            $attestationPath = [IO.Path]::GetFullPath($CleanProfileAttestation)
            $attestation = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
            $publishedExe = Join-Path $publishDirectory "Skilly.exe"
            $publishedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedExe).Hash.ToLowerInvariant()
            $valid = $attestation.schemaVersion -eq 1 `
                -and $attestation.gate -eq "clean-windows-profile" `
                -and $attestation.generatedBy -eq "scripts/Test-CleanWindowsProfile.ps1" `
                -and $attestation.artifact.sha256 -eq $publishedHash `
                -and $attestation.environment.windows11OrNewer -eq $true `
                -and $attestation.environment.architecture -eq "X64" `
                -and $attestation.environment.actualWindowsUserProfile `
                -and $attestation.environment.dotnetCommandAbsent -eq $true `
                -and $attestation.environment.systemDotnetRootsAbsent -eq $true `
                -and $attestation.observations.directExecutableLaunch -eq $true `
                -and $attestation.observations.secondActivationSignal -eq $true `
                -and $attestation.observations.secondLaunchExitCode -eq 0 `
                -and $attestation.observations.cleanShutdownExitCode -eq 0 `
                -and $attestation.observations.stateUnderLocalAppData -eq $true `
                -and $attestation.observations.workingDirectoryUnchanged -eq $true
            if (-not $valid) { throw "Attestation fields or artifact SHA-256 do not satisfy the clean-profile contract." }
            Copy-Item -LiteralPath $attestationPath -Destination $cleanProfileEvidence -Force
            Add-Result "clean-windows-profile" "PASSED" "Externally supplied live attestation verified the exact published artifact in an actual Windows 11 x64 profile with no dotnet command or system .NET roots, including direct launch, second activation, and clean shutdown." $cleanProfileEvidence
        } catch {
            Add-Result "clean-windows-profile" "FAILED" "The supplied clean-profile attestation was invalid: $($_.Exception.Message)"
        }
    }

    $finalWorktree = @(& git status --porcelain=v1 --untracked-files=all | Where-Object { $_ -notmatch '^\?\? \.claude/' })
    if (@(Compare-Object -ReferenceObject $initialWorktree -DifferenceObject $finalWorktree).Count -eq 0) {
        Add-Result "project-and-recovery-cleanliness" "PASSED" "Validation created no project worktree changes; deterministic/live fixtures verify recovery data cleanup."
    } else {
        Add-Result "project-and-recovery-cleanliness" "FAILED" "Validation changed the project worktree. Inspect git status before release."
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString("O")
        startedAt = $started.ToString("O")
        repository = $repo
        branch = $branch
        commit = $head
        sourceState = [ordered]@{
            workingTreeDirty = $initialWorktree.Count -gt 0
            initialChangeCount = $initialWorktree.Count
            note = if ($initialWorktree.Count -gt 0) { "Artifact includes uncommitted workspace changes on top of commit." } else { "Artifact source matched the recorded commit." }
        }
        artifact = if (Test-Path -LiteralPath (Join-Path $publishDirectory "Skilly.exe")) {
            $published = Get-Item -LiteralPath (Join-Path $publishDirectory "Skilly.exe")
            [ordered]@{ path = $published.FullName; sizeBytes = $published.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $published.FullName).Hash.ToLowerInvariant() }
        } else { $null }
        gates = $results
    }
    $jsonPath = Join-Path $OutputDirectory "release-validation.json"
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $markdownPath = Join-Path $OutputDirectory "release-validation.md"
    $lines = @(
        "# Skilly release validation",
        "",
        "- Generated: $($report.generatedAt)",
        "- Branch: ``$branch``",
        "- Base commit: ``$head``",
        "- Source state: $($report.sourceState.note) Initial change count: $($report.sourceState.initialChangeCount).",
        "- Artifact: ``$($report.artifact.path)``",
        "- SHA-256: ``$($report.artifact.sha256)``",
        "- Size: $($report.artifact.sizeBytes) bytes",
        "",
        "| Gate | Status | Detail |",
        "| --- | --- | --- |"
    )
    foreach ($result in $results) { $lines += "| $($result.gate) | $($result.status) | $($result.detail.Replace('|', '\|')) |" }
    $lines | Set-Content -LiteralPath $markdownPath -Encoding UTF8
    $results | Format-Table gate, status, detail -AutoSize | Out-String
    "Report: $markdownPath"

    $failed = @($results | Where-Object status -eq "FAILED").Count
    $skipped = @($results | Where-Object status -eq "SKIPPED").Count
    if ($failed -gt 0 -or (-not $AllowSkippedGates -and $skipped -gt 0)) { exit 1 }
}
finally {
    Pop-Location
}
