param([switch]$Publish, [string]$Version = '0.3.2')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if ($Version -notmatch '^0\.[0-9]+\.[0-9]+$') { throw 'Expected a pre-1.0 semantic version, e.g. 0.2.0' }
$taskDotnet = if (Test-Path './work/dotnet/dotnet.exe') { Join-Path $PSScriptRoot 'work/dotnet/dotnet.exe' } else { 'dotnet' }
$taskPython = if (Test-Path './work/venv/Scripts/python.exe') { Join-Path $PSScriptRoot 'work/venv/Scripts/python.exe' } else { 'python' }
& $taskDotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $taskDotnet test -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($Publish) {
    & $taskPython tools/bundle_assets.py
    if ($LASTEXITCODE -ne 0) { throw 'Asset preparation failed' }
    $taskPublish = Join-Path $PSScriptRoot ('work/publish-' + [guid]::NewGuid().ToString('N'))
    & $taskDotnet publish src/Ritual.App/Ritual.App.csproj -c Release -r win-x64 --self-contained true -p:ReleaseBundle=true "-p:Version=$Version" -p:DebugType=embedded -p:DebugSymbols=false "-p:PathMap=$PSScriptRoot=/_/src" -o $taskPublish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $taskFiles = @(Get-ChildItem -LiteralPath $taskPublish -File -Recurse)
    if ($taskFiles.Count -ne 1 -or $taskFiles[0].Name -ne 'Ritual.App.exe') { throw 'Expected exactly one executable in publish output' }
    $taskOut = Join-Path $PSScriptRoot "artifacts/$Version"
    New-Item -ItemType Directory -Force -Path $taskOut | Out-Null
    $taskName = "RitualChecker-$Version-win-x64.exe"
    Copy-Item -LiteralPath $taskFiles[0].FullName -Destination (Join-Path $taskOut $taskName) -Force
    $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskOut $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $taskOut 'SHA256SUMS.txt'), "$taskHash  $taskName`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Release ready: artifacts/$Version/$taskName"
}
