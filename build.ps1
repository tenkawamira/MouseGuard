param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'MouseGuard.WinUI\MouseGuard.WinUI.csproj'
$out = Join-Path $root 'dist\MouseGuard-2.0.0-final3'
$localDotnet = Join-Path $root '.tools\dotnet8\dotnet.exe'
$nugetConfig = Join-Path $root '.tools\NuGet.WinUI.config'

if (Test-Path $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
    $env:APPDATA = Join-Path $root '.tools\appdata'
    $env:LOCALAPPDATA = Join-Path $root '.tools\localappdata'
}
else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$configArgs = @()
if (Test-Path $nugetConfig) {
    $configArgs = @('--configfile', $nugetConfig)
}

if ($Publish) {
    if (Test-Path $out) {
        Remove-Item -LiteralPath $out -Recurse -Force
    }

    & $dotnet publish $project `
        -c Release `
        -r win-x64 `
        --self-contained true `
        @configArgs `
        -o $out
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }

    # The Windows App SDK publish target can omit loose XAML/PRI artifacts.
    # Copy them from the normal Release output so the unpackaged app can load its XAML.
    $releaseDir = Join-Path $root 'MouseGuard.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64'
    Copy-Item (Join-Path $releaseDir 'MouseGuard.pri') $out -Force
    Copy-Item (Join-Path $releaseDir '*.xbf') $out -Force
    $viewsOut = Join-Path $out 'Views'
    New-Item -ItemType Directory -Force $viewsOut | Out-Null
    Copy-Item (Join-Path $releaseDir 'Views\*.xbf') $viewsOut -Force

    Copy-Item (Join-Path $root 'README.md') $out -Force
    Copy-Item (Join-Path $root 'docs') $out -Recurse -Force
    Remove-Item (Join-Path $out 'MouseGuard.pdb') -Force -ErrorAction SilentlyContinue
    Write-Host "发布完成：$out" -ForegroundColor Green
}
else {
    & $dotnet build $project -c Debug @configArgs
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
}



