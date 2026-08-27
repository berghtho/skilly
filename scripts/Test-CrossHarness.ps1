[CmdletBinding()]
param(
    [string]$EvidencePath = "",
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-NativeCommand([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) { return $null }
    if ($command.Source.EndsWith(".ps1", [StringComparison]::OrdinalIgnoreCase)) {
        $cmd = [IO.Path]::ChangeExtension($command.Source, ".cmd")
        if (Test-Path -LiteralPath $cmd) { return $cmd }
    }
    return $command.Source
}

function Quote-Argument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-Harness([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$ExpectedToken) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Could not start $Name." }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        } catch {} finally { $process.Dispose() }
        return [pscustomobject]@{ harness = $Name; status = "FAILED"; detail = "Timed out after $TimeoutSeconds seconds." }
    }
    $output = $stdout.Result + "`n" + $stderr.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        return [pscustomobject]@{ harness = $Name; status = "FAILED"; detail = "Exited with code $exitCode." }
    }
    if ($output.IndexOf($ExpectedToken, [StringComparison]::Ordinal) -lt 0) {
        return [pscustomobject]@{ harness = $Name; status = "FAILED"; detail = "Session did not return the token stored only in the fixture Skill." }
    }
    return [pscustomobject]@{ harness = $Name; status = "PASSED"; detail = "Real session discovered and invoked the canonical fixture content." }
}

function Invoke-HarnessWithRetry([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory, [string]$ExpectedToken) {
    $first = Invoke-Harness $Name $Executable $Arguments $WorkingDirectory $ExpectedToken
    if ($first.status -eq "PASSED") { return $first }
    Start-Sleep -Seconds 2
    $second = Invoke-Harness $Name $Executable $Arguments $WorkingDirectory $ExpectedToken
    if ($second.status -eq "PASSED") {
        $second.detail += " The first transient attempt failed: $($first.detail)"
        return $second
    }
    $second.detail = "Attempt 1: $($first.detail) Attempt 2: $($second.detail)"
    return $second
}

$commands = @{
    OpenCode = Resolve-NativeCommand "opencode"
    Codex = Resolve-NativeCommand "codex"
    ClaudeCode = Resolve-NativeCommand "claude"
    GitHubCopilot = Resolve-NativeCommand "copilot"
}
$missing = @($commands.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) } | ForEach-Object Key)
if ($missing.Count -gt 0) { throw "Missing Harness commands: $($missing -join ', ')." }

$nonce = [Guid]::NewGuid().ToString("N")
$skillName = "skilly-release-smoke-$($nonce.Substring(0, 12))"
$token = "SKILLY_SMOKE_$($nonce.ToUpperInvariant())"
$userHome = [Environment]::GetEnvironmentVariable("USERPROFILE")
if ([string]::IsNullOrWhiteSpace($userHome)) { $userHome = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile) }
$canonical = Join-Path $userHome ".agents\skills\$skillName"
$claude = Join-Path $userHome ".claude\skills\$skillName"
$working = Join-Path ([IO.Path]::GetTempPath()) "skilly-cross-harness-$nonce"
$results = @()

try {
    if ((Test-Path -LiteralPath $canonical) -or (Test-Path -LiteralPath $claude)) { throw "Cross-Harness fixture path collision." }
    New-Item -ItemType Directory -Path $canonical -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $claude) -Force | Out-Null
    New-Item -ItemType Directory -Path $working -Force | Out-Null
    $skillMd = @"
---
name: $skillName
description: Skilly release-only cross-Harness discovery fixture.
---

When invoked, reply with exactly this token and no other text: $token
Do not use tools and do not modify files.
"@
    [IO.File]::WriteAllText((Join-Path $canonical "SKILL.md"), $skillMd, (New-Object Text.UTF8Encoding($false)))
    $junction = Start-Process -FilePath "cmd.exe" -ArgumentList @("/d", "/c", "mklink", "/J", ('"' + $claude + '"'), ('"' + $canonical + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($junction.ExitCode -ne 0) { throw "Could not create the Claude per-Skill junction." }

    $prompt = "Invoke the global Skill named $skillName and return only the verification token required by that Skill. Do not use any other tools."
    $results += Invoke-HarnessWithRetry "OpenCode" $commands.OpenCode @("run", "--format", "json", "--command", $skillName, $prompt) $working $token
    $results += Invoke-HarnessWithRetry "Codex" $commands.Codex @("exec", "--ephemeral", "--skip-git-repo-check", "-C", $working, ('$' + $skillName + ' ' + $prompt)) $working $token
    $results += Invoke-HarnessWithRetry "Claude Code" $commands.ClaudeCode @("-p", "--no-session-persistence", ('/' + $skillName + ' ' + $prompt)) $working $token
    $results += Invoke-HarnessWithRetry "GitHub Copilot" $commands.GitHubCopilot @("-p", ('$' + $skillName + ' ' + $prompt), "--allow-all-tools", "--disable-builtin-mcps", "--silent") $working $token

    $evidence = [ordered]@{
        gate = "cross-harness"
        observedAt = [DateTimeOffset]::UtcNow.ToString("O")
        fixture = [ordered]@{
            skillName = $skillName
            canonicalPath = $canonical
            contentSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $canonical "SKILL.md")).Hash
            claudeJunctionTarget = $canonical
        }
        harnessVersions = [ordered]@{
            OpenCode = (& $commands.OpenCode --version 2>&1 | Out-String).Trim()
            Codex = (& $commands.Codex --version 2>&1 | Out-String).Trim()
            ClaudeCode = (& $commands.ClaudeCode --version 2>&1 | Out-String).Trim()
            GitHubCopilot = (& $commands.GitHubCopilot --version 2>&1 | Out-String).Trim()
        }
        results = $results
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $parent = Split-Path -Parent $EvidencePath
        if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    }
    $results | Format-Table -AutoSize | Out-String
    if (@($results | Where-Object status -ne "PASSED").Count -gt 0) {
        throw "One or more real Harness sessions did not discover the fixture Skill."
    }
}
finally {
    if (Test-Path -LiteralPath $claude) { & cmd.exe /d /c rmdir ('"' + $claude + '"') | Out-Null }
    if (Test-Path -LiteralPath $canonical) { Remove-Item -LiteralPath $canonical -Recurse -Force }
    if (Test-Path -LiteralPath $working) { Remove-Item -LiteralPath $working -Recurse -Force }
}
