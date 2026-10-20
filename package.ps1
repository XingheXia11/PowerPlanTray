$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'PowerPlanTray.cs'
$m = [regex]::Match((Get-Content -Raw $src), 'AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)"\)')
if (-not $m.Success) { throw 'Cannot find AssemblyFileVersion in PowerPlanTray.cs' }
$ver = $m.Groups[1].Value

$stage = Join-Path $root ('_stage_' + $ver)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item (Join-Path $root 'PowerPlanTray.exe') (Join-Path $stage ("PowerPlanTray_$ver.exe"))

$files = @(
    'PowerPlanTray.cs', 'build.cmd', 'app.ico', 'app.manifest', 'PowerPlanTray.ini',
    'setup_plans.ps1', 'setup_plans.bat', 'make_icon.ps1', 'README.md', 'LICENSE'
)
foreach ($f in $files) { Copy-Item (Join-Path $root $f) (Join-Path $stage $f) }
Copy-Item (Join-Path $root 'assets') (Join-Path $stage 'assets') -Recurse

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist ("PowerPlanTray_$ver.zip")
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

Remove-Item $stage -Recurse -Force
Write-Host ("Created dist\PowerPlanTray_$ver.zip (exe: PowerPlanTray_$ver.exe)")
