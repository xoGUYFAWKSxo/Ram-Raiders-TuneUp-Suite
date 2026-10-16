# Builds publish\Installer\GalacticTuneUp_Setup.exe.
#
# Both projects are published into publish\ReleaseSuite. Publishing only the UI is not enough: the
# UI runs on the Windows Desktop runtime, which already contains PerformanceCounter, EventLog etc.,
# so the publish leaves those DLLs out, but the engine runs on the plain .NET runtime and needs them.
#
#   .\build-installer.ps1                 # framework-dependent (needs .NET 11 Desktop Runtime)
#   .\build-installer.ps1 -SelfContained  # bundles the runtime
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = 'publish\ReleaseSuite'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$rid = if ($SelfContained) { @('-r', 'win-x64', '--self-contained') } else { @() }

foreach ($proj in 'TuneUpUI\TuneUpUI.csproj', 'TuneUpEngine\TuneUpEngine.csproj') {
    dotnet publish $proj -c Release -o $out @rid
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# Every assembly the engine's deps.json lists must be in the folder, or the installed service
# fails the first time it needs one.
$deps = Get-Content "$out\TuneUpEngine.deps.json" -Raw | ConvertFrom-Json
$missing = foreach ($lib in $deps.targets.PSObject.Properties.Value.PSObject.Properties) {
    foreach ($section in 'runtime', 'runtimeTargets') {
        if ($lib.Value.$section) {
            foreach ($path in $lib.Value.$section.PSObject.Properties.Name) {
                $local = if ($section -eq 'runtime') { Split-Path $path -Leaf } else { $path }
                if (-not (Test-Path (Join-Path $out $local))) { $local }
            }
        }
    }
}
if ($missing) { throw "Engine files missing from ${out}:`n$($missing -join "`n")" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc /Q installer.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

# The installer is not code-signed, so publish its SHA-256 next to it: anyone can check the file
# they downloaded is the one that was built. Same format as sha256sum ("<hash> *<file>").
$setup = Get-Item 'publish\Installer\GalacticTuneUp_Setup.exe'
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$($setup.Name)" | Set-Content "$($setup.FullName).sha256" -Encoding ascii
Write-Host "SHA-256: $hash"
$setup
Get-Item "$($setup.FullName).sha256"
