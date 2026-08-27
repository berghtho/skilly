[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ExePath = [IO.Path]::GetFullPath($ExePath)
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) { throw "Skilly.exe was not found at '$ExePath'." }
if ([Environment]::OSVersion.Version.Build -lt 22000) { throw "This attestation requires Windows 11 (build 22000 or newer)." }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw "This attestation requires Windows x64." }
if (Get-Command dotnet -ErrorAction SilentlyContinue) { throw "dotnet is available in this profile; this is not a profile without .NET." }

$dotnetRoots = @(
    (Join-Path $env:ProgramFiles "dotnet"),
    $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} "dotnet" } else { $null }),
    $(if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { $null }),
    $(if ($env:DOTNET_ROOT_X64) { $env:DOTNET_ROOT_X64 } else { $null })
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$presentRoots = @($dotnetRoots | Where-Object { Test-Path -LiteralPath $_ })
if ($presentRoots.Count -gt 0) { throw "A system .NET root exists: $($presentRoots -join ', '). This is not a no-.NET attestation environment." }

$existing = @(Get-Process -Name "Skilly" -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) { throw "Close existing Skilly processes before running the clean-profile attestation." }

$workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ("skilly-clean-profile-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workingDirectory | Out-Null
$first = $null
$second = $null
try {
    $first = Start-Process -FilePath $ExePath -WorkingDirectory $workingDirectory -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(2)
    do {
        Start-Sleep -Milliseconds 200
        $first.Refresh()
        if ($first.HasExited) { throw "The direct Skilly.exe launch exited prematurely with code $($first.ExitCode)." }
    } while ($first.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($first.MainWindowHandle -eq 0) { throw "The direct Skilly.exe launch did not create a window." }

    $logRoot = Join-Path $env:LOCALAPPDATA "Skilly\logs"
    $logOffsets = @{}
    if (Test-Path -LiteralPath $logRoot) {
        Get-ChildItem -LiteralPath $logRoot -Filter "skilly-*.log" -File | ForEach-Object { $logOffsets[$_.FullName] = $_.Length }
    }
    $second = Start-Process -FilePath $ExePath -WorkingDirectory $workingDirectory -PassThru
    if (-not $second.WaitForExit(20000)) { throw "The second activation did not exit within 20 seconds." }
    if ($second.ExitCode -ne 0) { throw "The second activation exited with code $($second.ExitCode)." }
    $first.Refresh()
    if ($first.HasExited) { throw "The first instance exited during second activation." }

    $focusDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $logText = if (Test-Path -LiteralPath $logRoot) {
            (@(Get-ChildItem -LiteralPath $logRoot -Filter "skilly-*.log" -File | ForEach-Object {
                $stream = [IO.File]::Open($_.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try {
                    $offset = if ($logOffsets.ContainsKey($_.FullName)) { [long]$logOffsets[$_.FullName] } else { 0 }
                    [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
                    $reader = [IO.StreamReader]::new($stream)
                    try { $reader.ReadToEnd() } finally { $reader.Dispose() }
                } finally { $stream.Dispose() }
            }) -join "`n")
        } else { "" }
        if ($logText.Contains("focus signal sent=True", [StringComparison]::Ordinal)) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $focusDeadline)
    if (-not $logText.Contains("focus signal sent=True", [StringComparison]::Ordinal)) { throw "The second launch did not record a successful activation signal." }

    if (-not $first.CloseMainWindow()) { throw "The primary window did not accept a normal close request." }
    if (-not $first.WaitForExit(20000)) { throw "Skilly did not terminate within 20 seconds of normal window close." }
    if ($first.ExitCode -ne 0) { throw "Skilly shutdown exited with code $($first.ExitCode)." }
    if (@(Get-ChildItem -LiteralPath $workingDirectory -Force).Count -ne 0) { throw "Skilly changed its launch working directory." }

    $statePath = Join-Path $env:LOCALAPPDATA "Skilly\state.json"
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "Skilly did not write authority state under this profile's LocalAppData." }
    $artifact = Get-Item -LiteralPath $ExePath
    $evidence = [ordered]@{
        schemaVersion = 1
        gate = "clean-windows-profile"
        observedAt = [DateTimeOffset]::UtcNow.ToString("O")
        generatedBy = "scripts/Test-CleanWindowsProfile.ps1"
        artifact = [ordered]@{
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash.ToLowerInvariant()
            sizeBytes = $artifact.Length
        }
        environment = [ordered]@{
            windows11OrNewer = $true
            windowsVersion = [Environment]::OSVersion.Version.ToString()
            architecture = "X64"
            actualWindowsUserProfile = $env:USERPROFILE
            dotnetCommandAbsent = $true
            systemDotnetRootsAbsent = $true
        }
        observations = [ordered]@{
            directExecutableLaunch = $true
            mainWindowCreated = $true
            secondActivationSignal = $true
            secondLaunchExitCode = $second.ExitCode
            cleanShutdownExitCode = $first.ExitCode
            stateUnderLocalAppData = $true
            workingDirectoryUnchanged = $true
        }
    }
    $parent = Split-Path -Parent $EvidencePath
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
    $EvidencePath
}
finally {
    if ($second -and -not $second.HasExited) { $second.Kill() }
    if ($first -and -not $first.HasExited) { $first.Kill() }
    if ($second) { $second.Dispose() }
    if ($first) { $first.Dispose() }
    if (Test-Path -LiteralPath $workingDirectory) { Remove-Item -LiteralPath $workingDirectory -Recurse -Force }
}
