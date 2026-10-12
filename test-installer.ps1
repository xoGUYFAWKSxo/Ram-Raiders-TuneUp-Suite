# Installer test: install, upgrade over an existing install, and uninstall, checking after each
# step what is installed, running and left behind. Run from an ADMINISTRATOR PowerShell after
# build-installer.ps1:
#
#   .\test-installer.ps1 -Phase Install     # fresh install (stops any running TuneUp first)
#   .\test-installer.ps1 -Phase Upgrade     # run the same setup again over the installed copy
#   .\test-installer.ps1 -Phase Uninstall   # uninstall, then check nothing is left
#
# Leave the app open before -Phase Upgrade / -Phase Uninstall: setup must cope with it running.
# Uninstalling reverts every change TuneUp recorded on this PC (that is part of what is tested).
# Results go to the console and to publish\InstallerTest\<phase>.txt; the engine log is copied
# there before uninstall deletes it.
param([Parameter(Mandatory)][ValidateSet('Install', 'Upgrade', 'Uninstall')][string]$Phase)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an administrator PowerShell.'
}

$setup = 'publish\Installer\RamRaidersTuneUpSuite_Setup.exe'
$appDir = Join-Path $env:ProgramFiles 'Ram Raiders TuneUp Suite'
$dataDir = Join-Path $env:ProgramData 'RamRaidersTuneUpSuite'
$service = 'TuneUpEngineService'
$outDir = 'publish\InstallerTest'
New-Item -ItemType Directory -Force $outDir | Out-Null
$report = Join-Path $outDir "$($Phase.ToLower()).txt"
$results = [System.Collections.Generic.List[string]]::new()
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $line = "{0}  {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { "  ($detail)" } else { '' })
    $script:results.Add($line)
    if (-not $ok) { $script:failed++ }
    Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Note([string]$text) { $script:results.Add("      $text"); Write-Host "      $text" }

function EngineProcesses { @(Get-CimInstance Win32_Process -Filter "Name='TuneUpEngine.exe'") }
function ServiceInfo { Get-CimInstance Win32_Service -Filter "Name='$service'" }

function CheckInstalled([string]$label) {
    Check "$label`: TuneUpUI.exe installed" (Test-Path "$appDir\TuneUpUI.exe")
    Check "$label`: TuneUpEngine.exe installed" (Test-Path "$appDir\TuneUpEngine.exe")
    $svc = ServiceInfo
    Check "$label`: service registered" ($null -ne $svc)
    if ($svc) {
        Check "$label`: service running" ($svc.State -eq 'Running') $svc.State
        Check "$label`: service starts automatically" ($svc.StartMode -eq 'Auto') $svc.StartMode
        Check "$label`: service runs the installed engine" ($svc.PathName -like "*$appDir\TuneUpEngine.exe*--service*") $svc.PathName
        $procs = @(EngineProcesses)
        Check "$label`: exactly one engine process" ($procs.Count -eq 1) "$($procs.Count) running"
        if ($procs.Count -ge 1) {
            Check "$label`: that process is the service's" ($procs[0].ProcessId -eq $svc.ProcessId) "pid $($procs[0].ProcessId), service pid $($svc.ProcessId)"
        }
    }
    $dlls = Get-ChildItem "$appDir\*.dll" -ErrorAction SilentlyContinue | Measure-Object
    Note "$($dlls.Count) DLLs in $appDir"
}

function RunSetup([string[]]$arguments, [string]$log) {
    $p = Start-Process -FilePath $setup -ArgumentList ($arguments + "/LOG=`"$log`"") -Wait -PassThru
    Check "setup exit code 0" ($p.ExitCode -eq 0) "exit $($p.ExitCode), log $log"
}

switch ($Phase) {
    'Install' {
        if (-not (Test-Path $setup)) { throw "Build the installer first: .\build-installer.ps1" }
        if (ServiceInfo) { throw 'TuneUp is already installed. Use -Phase Upgrade, or -Phase Uninstall first.' }
        # A dev build's engine would hold the pipe and keep the service from starting.
        Get-Process TuneUpUI, TuneUpEngine -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 1
        Note "setup: $((Get-Item $setup).LastWriteTime)"

        RunSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') ((Resolve-Path $outDir).Path + '\install-setup.log')
        Start-Sleep -Seconds 3
        CheckInstalled 'install'
        $s = Get-Content "$dataDir\engine_settings.json" -Raw -ErrorAction SilentlyContinue
        Note "engine settings after first start: $(if ($s) { $s -replace '\s+', ' ' } else { '(none yet: defaults, all modules off)' })"
    }
    'Upgrade' {
        $before = ServiceInfo
        if (-not $before) { throw 'TuneUp is not installed. Run -Phase Install first.' }
        $oldPid = $before.ProcessId
        RunSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') ((Resolve-Path $outDir).Path + '\upgrade-setup.log')
        Start-Sleep -Seconds 3
        CheckInstalled 'upgrade'
        $after = ServiceInfo
        Check 'upgrade: engine was restarted (new process)' ($after.ProcessId -ne $oldPid) "pid $oldPid -> $($after.ProcessId)"
        $setupTime = (Get-Item $setup).LastWriteTime
        $engineTime = (Get-Item "$appDir\TuneUpEngine.exe").LastWriteTime
        Note "installed engine file time $engineTime (setup built $setupTime)"
    }
    'Uninstall' {
        $uninstaller = Get-ChildItem "$appDir\unins*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $uninstaller) { throw "No uninstaller in $appDir" }
        if (Test-Path "$dataDir\change_journal.json") {
            Copy-Item "$dataDir\change_journal.json" $outDir -Force
            Note "change journal before uninstall copied to $outDir"
        }
        $p = Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
        Check 'uninstaller exit code 0' ($p.ExitCode -eq 0) "exit $($p.ExitCode)"
        # The uninstaller hands off to a copy of itself and returns early; wait for the files to go.
        for ($i = 0; $i -lt 30 -and (Test-Path $appDir); $i++) { Start-Sleep -Seconds 1 }

        Check 'uninstall: service removed' ($null -eq (ServiceInfo))
        $procs = @(EngineProcesses)
        Check 'uninstall: no engine process left running' ($procs.Count -eq 0) (($procs | ForEach-Object { "pid $($_.ProcessId) $($_.ExecutablePath)" }) -join '; ')
        $ui = @(Get-Process TuneUpUI -ErrorAction SilentlyContinue)
        Check 'uninstall: no TuneUp window left open' ($ui.Count -eq 0) "$($ui.Count) running"
        Check 'uninstall: program folder removed' (-not (Test-Path $appDir))
        Check 'uninstall: ProgramData folder removed' (-not (Test-Path $dataDir))
        Check 'uninstall: no Start menu shortcut left' (-not (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Ram Raiders TuneUp Suite"))
        $sysMain = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\SysMain' -ErrorAction SilentlyContinue).Start
        Note "SysMain start type after uninstall: $sysMain (2 = Automatic)"
    }
}

# Keep the engine log: uninstall deletes it with ProgramData.
Get-ChildItem "$dataDir\logs\engine-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $outDir "engine-after-$($Phase.ToLower()).log") -Force }

$summary = if ($failed -eq 0) { "ALL PASSED ($Phase)" } else { "$failed CHECK(S) FAILED ($Phase)" }
$results.Add($summary)
$results | Set-Content $report -Encoding utf8
Write-Host $summary -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "Report: $report"
