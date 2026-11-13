<#
.SYNOPSIS
    Installs, upgrades or removes Skimr as a Windows service.

.DESCRIPTION
    Run from an unpacked win-x64 release folder (Skimr.exe, wwwroot\, appsettings.json) in an
    elevated PowerShell (Windows PowerShell 5.1 or PowerShell 7):

        .\install.ps1 [-Port 8080] [-Setting @{ 'SKIMR__Ai__Provider' = 'None' }]
        .\install.ps1 -Uninstall            # keeps C:\ProgramData\Skimr
        .\install.ps1 -Uninstall -Purge     # also deletes data and settings
        .\install.ps1 -WhatIf               # shows what would happen

    Program files go to C:\Program Files\Skimr; data to C:\ProgramData\Skimr\data. The service
    runs as the virtual account NT SERVICE\Skimr, which alone (with SYSTEM and Administrators)
    can read the data folder. Settings are the service's environment variables (SKIMR__Section__Key);
    re-running keeps them and adds any given with -Setting.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateRange(1, 65535)]
    [int] $Port = 8080,

    [hashtable] $Setting = @{},

    [switch] $Uninstall,

    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'Skimr'
$Account = "NT SERVICE\$ServiceName"
$AppDir = Join-Path $env:ProgramFiles 'Skimr'
$RootDir = Join-Path $env:ProgramData 'Skimr'
$DataDir = Join-Path $RootDir 'data'
$CacheDir = Join-Path $RootDir 'cache'
$ServiceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$SourceDir = $PSScriptRoot

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Sc {
    param([string[]] $Arguments)
    $output = & sc.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments -join ' ') failed: $output" }
}

function Stop-SkimrService {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped' -and $PSCmdlet.ShouldProcess($ServiceName, 'Stop service')) {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    return $service
}

if (-not $WhatIfPreference -and -not (Test-Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator).'
}

if ($Purge -and -not $Uninstall) {
    throw '-Purge only goes with -Uninstall.'
}

# ---- Uninstall ------------------------------------------------------------------------------

if ($Uninstall) {
    $service = Stop-SkimrService
    if ($service -and $PSCmdlet.ShouldProcess($ServiceName, 'Delete service')) {
        Invoke-Sc @('delete', $ServiceName)
    }
    if ((Test-Path $AppDir) -and $PSCmdlet.ShouldProcess($AppDir, 'Delete program files')) {
        Remove-Item -Path $AppDir -Recurse -Force
    }
    if ($Purge) {
        if ((Test-Path $RootDir) -and $PSCmdlet.ShouldProcess($RootDir, 'Delete data and settings')) {
            Remove-Item -Path $RootDir -Recurse -Force
        }
        if (-not $WhatIfPreference) { Write-Host 'Skimr removed, including its data.' }
    }
    else {
        if ((Test-Path $CacheDir) -and $PSCmdlet.ShouldProcess($CacheDir, 'Delete cache')) {
            Remove-Item -Path $CacheDir -Recurse -Force
        }
        if (-not $WhatIfPreference) { Write-Host "Skimr removed. Data kept in $DataDir (add -Purge to delete it)." }
    }
    return
}

# ---- Install or upgrade -----------------------------------------------------------------------

if (-not (Test-Path (Join-Path $SourceDir 'Skimr.exe')) -or -not (Test-Path (Join-Path $SourceDir 'wwwroot'))) {
    throw 'Run this from an unpacked Skimr win-x64 release folder (Skimr.exe and wwwroot\ next to it).'
}

$existing = Stop-SkimrService

if ($PSCmdlet.ShouldProcess($AppDir, 'Copy program files')) {
    if (Test-Path $AppDir) { Remove-Item -Path $AppDir -Recurse -Force }
    New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
    Get-ChildItem -Path $SourceDir -Force |
        Where-Object { $_.Name -notin @('install.ps1', 'install.sh', 'skimr.service') } |
        Copy-Item -Destination $AppDir -Recurse -Force
}

if (-not $existing -and $PSCmdlet.ShouldProcess($ServiceName, 'Create service')) {
    New-Service -Name $ServiceName -BinaryPathName ('"{0}"' -f (Join-Path $AppDir 'Skimr.exe')) `
        -DisplayName 'Skimr feed reader' -Description 'Self-hosted RSS/Atom reader.' -StartupType Automatic | Out-Null
    # A virtual account: no password, no rights beyond what is granted below.
    Invoke-Sc @('config', $ServiceName, 'obj=', $Account)
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')
}

if ($PSCmdlet.ShouldProcess($RootDir, 'Create data folder, limited to the service, SYSTEM and Administrators')) {
    foreach ($dir in @($RootDir, $DataDir, $CacheDir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    # Replace inherited permissions (Users can read ProgramData by default).
    & icacls.exe $RootDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' "${Account}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed on $RootDir" }
}

# Settings: keep what's there, then apply defaults for anything missing and -Setting on top.
$environment = [ordered]@{}
$current = (Get-ItemProperty -Path $ServiceKey -Name Environment -ErrorAction SilentlyContinue)
if ($current) {
    foreach ($line in $current.Environment) {
        $name, $value = $line -split '=', 2
        if ($name) { $environment[$name] = $value }
    }
}
if (-not $environment.Contains('ASPNETCORE_HTTP_PORTS')) { $environment['ASPNETCORE_HTTP_PORTS'] = "$Port" }
$environment['SKIMR__Storage__DataDirectory'] = $DataDir
$environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $CacheDir
foreach ($key in $Setting.Keys) { $environment[$key] = [string]$Setting[$key] }
$Port = [int]($environment['ASPNETCORE_HTTP_PORTS'] -split '[;,]')[0]

if ($PSCmdlet.ShouldProcess($ServiceKey, "Set service environment ($($environment.Count) values)")) {
    $lines = [string[]]($environment.Keys | ForEach-Object { "$_=$($environment[$_])" })
    New-ItemProperty -Path $ServiceKey -Name Environment -PropertyType MultiString -Value $lines -Force | Out-Null
}

if (-not $PSCmdlet.ShouldProcess($ServiceName, 'Start service')) {
    return
}

Start-Service -Name $ServiceName

$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    Start-Sleep -Seconds 1
    try {
        $ready = (Invoke-WebRequest -Uri "http://localhost:$Port/health/ready" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200
    }
    catch {
        # Still starting.
    }
}

if (-not $ready) {
    Write-Error "Skimr didn't become ready on port $Port. See the logs in $DataDir\logs and the Windows Application event log."
    exit 1
}

if ($existing) {
    Write-Host "Skimr upgraded and running on port $Port."
}
else {
    Write-Host "Skimr is running on http://localhost:$Port (open the port in Windows Firewall to reach it from other machines)."
    $log = Get-ChildItem -Path (Join-Path $DataDir 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        $code = Select-String -Path $log.FullName -Pattern 'setup code: ([A-Z0-9-]+)' | Select-Object -Last 1
        if ($code) { Write-Host "Open /setup in your browser and enter the setup code: $($code.Matches[0].Groups[1].Value)" }
    }
}
