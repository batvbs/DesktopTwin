# 本地打包脚本
#
# 用法：
#   pwsh build/build.ps1              # 框架依赖，需要目标机装 .NET 10 桌面运行时
#   pwsh build/build.ps1 -SelfContained   # 自带运行时，体积更大但开箱即用
#
# 产物：artifacts/DesktopTwin-<版本>-win-x64.zip

[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/DesktopTwin/DesktopTwin.csproj"
$publishDir = Join-Path $repoRoot "artifacts/publish"
$artifactsDir = Join-Path $repoRoot "artifacts"

Write-Host "仓库根目录：$repoRoot"

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

# 单文件发布：WPF 的 XAML 资源需要 IncludeNativeLibrariesForSelfExtract 才能打进单文件
$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", "win-x64",
    "-o", $publishDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "--nologo"
)

if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
}
else {
    $publishArgs += @("--self-contained", "false")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

$version = (Get-Item $project | Select-String -Pattern "<Version>([^<]+)</Version>").Matches[0].Groups[1].Value
if (-not $version) {
    $version = "0.0.0"
}

$zipPath = Join-Path $artifactsDir "DesktopTwin-$version-win-x64.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
Write-Host ""
Write-Host "完成：$zipPath"
