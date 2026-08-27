[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$PrivateGitHubUrl = $env:SKILLY_LIVE_PRIVATE_GITHUB_URL,
    [string]$SkillsSource = $env:SKILLY_LIVE_SKILLS_SOURCE,
    [string]$ApmSource = $env:SKILLY_LIVE_APM_SOURCE,
    [switch]$RunCrossHarness,
    [switch]$RequireAllGates
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

function Invoke-LiveTest([string]$Gate, [string]$FullyQualifiedName, [hashtable]$Environment, [string]$EvidenceName) {
    $saved = @{}
    try {
        foreach ($key in $Environment.Keys) {
            $saved[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key])
        }
        $saved["SKILLY_LIVE_EVIDENCE_DIRECTORY"] = [Environment]::GetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY")
        [Environment]::SetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY", $evidenceDirectory)
        & dotnet test (Join-Path $repo "Skilly.slnx") --no-restore --no-build --filter "FullyQualifiedName=$FullyQualifiedName" --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) {
            Add-Result $Gate "FAILED" "Live test failed with exit code $LASTEXITCODE."
            return
        }
        $evidence = Join-Path $evidenceDirectory ($EvidenceName + ".json")
        if (-not (Test-Path -LiteralPath $evidence)) {
            Add-Result $Gate "FAILED" "Live test passed but did not write revision evidence."
            return
        }
        Add-Result $Gate "PASSED" "Live compatibility and topology postconditions passed." $evidence
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
    $initialWorktree = @(& git status --porcelain=v1 --untracked-files=all)
    $started = [DateTimeOffset]::UtcNow

    $buildPassed = Invoke-CommandGate "release-build" {
        & dotnet build "Skilly.slnx" -c Release
    } "Release build completed without errors."

    if ($buildPassed) {
        $trx = Join-Path $testResultsDirectory "deterministic.trx"
        & dotnet test "Skilly.slnx" -c Release --no-restore --no-build `
            --filter "Category!=LiveGitHubPreRelease&Category!=LiveSkillsCliPreRelease&Category!=LiveApmPreRelease" `
            --logger "trx;LogFileName=$trx" --results-directory $testResultsDirectory
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $trx)) {
            [xml]$testRun = Get-Content -LiteralPath $trx -Raw
            $counters = $testRun.TestRun.ResultSummary.Counters
            Add-Result "deterministic-suite" "PASSED" "$($counters.passed) passed, $($counters.failed) failed, $($counters.notExecuted) not executed." $trx
        } else {
            Add-Result "deterministic-suite" "FAILED" "Deterministic tests failed with exit code $LASTEXITCODE." $(if (Test-Path -LiteralPath $trx) { $trx } else { "" })
        }
    } else {
        Add-Result "deterministic-suite" "FAILED" "Not run because the Release build failed."
    }

    if ($buildPassed) {
        & dotnet publish "src\Skilly\Skilly.csproj" -c Release -r win-x64 --self-contained true --no-restore -o $publishDirectory
        $exe = Join-Path $publishDirectory "Skilly.exe"
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $exe)) {
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash.ToLowerInvariant()
            $size = (Get-Item -LiteralPath $exe).Length
            Add-Result "self-contained-publish" "PASSED" "Published win-x64 single-file Skilly.exe ($size bytes, SHA-256 $hash)." $exe
        } else {
            Add-Result "self-contained-publish" "FAILED" "Self-contained publish failed or Skilly.exe was absent."
        }
    }

    if (Get-Command gh -ErrorAction SilentlyContinue) {
        & gh auth status *> $null
        if ($LASTEXITCODE -eq 0) {
            $cursorRevision = (& gh api "repos/cursor/plugins/commits/main" --jq .sha).Trim()
            if ($LASTEXITCODE -eq 0 -and $cursorRevision -match '^[0-9a-f]{40}$') {
                Invoke-LiveTest "cursor-pstack" `
                    "Skilly.App.Tests.LiveGitHubPreReleaseTests.Current_Cursor_pstack_source_is_complete_at_one_immutable_revision" `
                    @{ SKILLY_RUN_LIVE_GITHUB_TESTS = "1"; SKILLY_EXPECTED_CURSOR_REVISION = $cursorRevision } `
                    "cursor-pstack"
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
            @{ SKILLY_RUN_LIVE_GITHUB_TESTS = "1"; SKILLY_LIVE_PRIVATE_GITHUB_URL = $PrivateGitHubUrl } `
            "private-github"
    }

    if ([string]::IsNullOrWhiteSpace($SkillsSource)) {
        Add-Result "skills-provider" "SKIPPED" "Prerequisite missing: SKILLY_LIVE_SKILLS_SOURCE was not supplied."
    } else {
        Invoke-LiveTest "skills-provider" `
            "Skilly.App.Tests.LiveSkillsCliPreReleaseTests.Pinned_provider_supports_inspect_install_read_only_check_and_uninstall_in_an_isolated_home" `
            @{ SKILLY_RUN_LIVE_SKILLS_TESTS = "1"; SKILLY_LIVE_SKILLS_SOURCE = $SkillsSource } `
            "skills-provider"
    }

    if ([string]::IsNullOrWhiteSpace($ApmSource)) {
        Add-Result "apm-provider" "SKIPPED" "Prerequisite missing: SKILLY_LIVE_APM_SOURCE was not supplied."
    } else {
        Invoke-LiveTest "apm-provider" `
            "Skilly.App.Tests.LiveApmPreReleaseTests.Pinned_Microsoft_APM_supports_the_adapter_contract_in_an_isolated_home" `
            @{ SKILLY_RUN_LIVE_APM_TESTS = "1"; SKILLY_LIVE_APM_SOURCE = $ApmSource } `
            "apm-provider"
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

    Add-Result "clean-windows-profile" "SKIPPED" "No disposable clean Windows 11 x64 profile without .NET was available. Packaged tests disable runtime lookup, but this does not fabricate the live clean-profile gate."

    $finalWorktree = @(& git status --porcelain=v1 --untracked-files=all)
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
        "- Commit: ``$head``",
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
    if ($failed -gt 0 -or ($RequireAllGates -and $skipped -gt 0)) { exit 1 }
}
finally {
    Pop-Location
}
