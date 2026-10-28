# Publishes the app and runner into installer\publish, then compiles installer\Output\RegWatch-Setup-<version>.exe.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path $here
$publish = Join-Path $here 'publish'

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in 'RegWatch.App', 'RegWatch.Runner') {
    dotnet publish (Join-Path $repo "src\$project") -c Release -r win-x64 --self-contained false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

& $iscc "/DAppVersion=$version" (Join-Path $here 'RegWatch.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Get-Item (Join-Path $here "Output\RegWatch-Setup-$version.exe")
