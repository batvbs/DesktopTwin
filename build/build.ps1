# 本地打包脚本
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build/build.ps1
#
# 一次产出**两个版本**，放在同一个 zip 里：
#   v<版本> 自带运行时/      自带 .NET 运行时，开箱即用，体积大
#   v<版本> 需要 .NET 运行时/ 框架依赖，体积小，但目标机要先装 .NET 10 桌面运行时
#
# 产物：artifacts/DesktopTwin-<版本>-win-x64.zip

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/DesktopTwin/DesktopTwin.csproj"
$artifactsDir = Join-Path $repoRoot "artifacts"
$stageDir = Join-Path $artifactsDir "stage"

Write-Host "仓库根目录：$repoRoot"

$version = (Get-Item $project | Select-String -Pattern "<Version>([^<]+)</Version>").Matches[0].Groups[1].Value
if (-not $version) {
    $version = "0.0.0"
}

# 目录名带版本号：用户解压后能一眼看出手上是哪一版，
# 也避免同一台机器上放了两个版本时分不清哪个是新的。
$standaloneDir = Join-Path $stageDir "v$version 自带运行时"
$frameworkDir = Join-Path $stageDir "v$version 需要 .NET 运行时"

if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}

# ── 公共发布参数 ─────────────────────────────────────────
# 单文件发布：WPF 的 XAML 资源需要 IncludeNativeLibrariesForSelfExtract 才能打进单文件
function Publish-Variant {
    param(
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][bool]$SelfContained
    )

    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", "win-x64",
        "-o", $OutputDir,
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:DebugType=none",
        "--nologo"
    )

    if ($SelfContained) {
        $publishArgs += @("--self-contained", "true")
        # 压缩只在自带运行时时可用，框架依赖模式下 dotnet 会直接报 NETSDK1176
        $publishArgs += @("-p:EnableCompressionInSingleFile=true")
    }
    else {
        $publishArgs += @("--self-contained", "false")
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（SelfContained=$SelfContained），退出码 $LASTEXITCODE"
    }

    # 调试符号对最终用户没用，还会暴露源码路径和符号名。
    Get-ChildItem $OutputDir -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Host ""
Write-Host "=== 1/3 发布：自带运行时 ==="
Publish-Variant -OutputDir $standaloneDir -SelfContained $true

Write-Host ""
Write-Host "=== 2/3 发布：框架依赖 ==="
Publish-Variant -OutputDir $frameworkDir -SelfContained $false

# ── 说明文件 ─────────────────────────────────────────────
Write-Host ""
Write-Host "=== 3/3 打包 ==="

$readmeTemplate = Join-Path $PSScriptRoot "使用说明.txt"
if (-not (Test-Path $readmeTemplate)) {
    throw "缺少说明文件模板：$readmeTemplate"
}

# 说明文件与 CI 共用同一份模板，避免两边写的版本号/目录名对不上。
$readme = (Get-Content $readmeTemplate -Raw -Encoding UTF8).Replace("{{VERSION}}", $version)

# 用 UTF-8 带 BOM 写：记事本在旧版本 Windows 上靠 BOM 判断编码，
# 没有 BOM 时中文会显示成乱码。
$readmePath = Join-Path $stageDir "使用说明.txt"
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))

$zipPath = Join-Path $artifactsDir "DesktopTwin-$version-win-x64.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

# 条目名是中文，这里依赖 Compress-Archive 写出正确的 UTF-8 标志位。
# 已实测：生成后每个条目的通用标志位含 0x800（UTF-8），解压工具能正确显示中文。
# 如果将来换到别的环境发现乱码，改用 ZipFile.CreateFromDirectory 显式传 Encoding.UTF8。
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
$standaloneExe = Join-Path $standaloneDir "DesktopTwin.exe"
$frameworkExe = Join-Path $frameworkDir "DesktopTwin.exe"

Write-Host ""
Write-Host "完成：$zipPath"
Write-Host ("  自带运行时 DesktopTwin.exe：{0:N1} MB" -f ((Get-Item $standaloneExe).Length / 1MB))
Write-Host ("  框架依赖   DesktopTwin.exe：{0:N1} MB" -f ((Get-Item $frameworkExe).Length / 1MB))
Write-Host "  压缩包：$zipSizeMb MB"
Write-Host "  SHA256：$hash"
