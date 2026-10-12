# Puts the current checkout into the installed app: builds the installer, then installs it over
# the existing copy (or fresh, if TuneUp isn't installed) and runs the installer checks.
#
#   .\update-installed.ps1
#
# Run from a normal PowerShell; it asks for administrator rights (UAC) for the install step.
# The installed engine only trusts the UI in Program Files, so this is how every build is tested;
# there are no separate dev-test folders. Report: publish\InstallerTest\<install|upgrade>.txt
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

& .\build-installer.ps1 | Out-Null

$installed = $null -ne (Get-Service TuneUpEngineService -ErrorAction SilentlyContinue)
$phase = if ($installed) { 'Upgrade' } else { 'Install' }
$commit = (git rev-parse --short HEAD) + ' on ' + (git branch --show-current)
Write-Host "Installing $commit ($phase)..."

# A leftover report from an earlier run must not pass for this one.
$report = Join-Path $PSScriptRoot "publish\InstallerTest\$($phase.ToLower()).txt"
Remove-Item $report -ErrorAction SilentlyContinue

Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $PSScriptRoot 'test-installer.ps1'), '-Phase', $phase)

if (-not (Test-Path $report)) { throw "No installer report at $report (was the UAC prompt declined?)" }
$summary = Get-Content $report -Tail 1
Write-Host $summary
if ($summary -notlike 'ALL PASSED*') {
    Get-Content $report | Where-Object { $_ -like 'FAIL*' } | ForEach-Object { Write-Host $_ }
    exit 1
}
