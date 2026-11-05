# 本地打包脚本
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build/build.ps1
#
# 一次产出**两个版本**，平铺在同一个 zip 里（不套文件夹）：
#   桌面分身-<版本>.exe        自带 .NET 运行时，开箱即用
#   桌面分身-<版本>-NET10.exe  框架依赖，需先装 .NET 10 桌面运行时
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
$publishRoot = Join-Path $artifactsDir "publish"
$stageDir = Join-Path $artifactsDir "stage"

Write-Host "仓库根目录：$repoRoot"

$version = (Get-Item $project | Select-String -Pattern "<Version>([^<]+)</Version>").Matches[0].Groups[1].Value
if (-not $version) {
    $version = "0.0.0"
}

# 文件名直接用中文：用户在下载文件夹里看到的就是「桌面分身-0.2.2.exe」，
# 不用先解压到文件夹再猜哪个是哪个。程序内部不依赖自身文件名，
# 改名不影响单实例互斥体、设置路径和图标（都按程序集名走）。
$standaloneName = "桌面分身-$version.exe"
$frameworkName = "桌面分身-$version-NET10.exe"

foreach ($dir in @($publishRoot, $stageDir)) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}

# ── 发布 ────────────────────────────────────────────────
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
Publish-Variant -OutputDir (Join-Path $publishRoot "standalone") -SelfContained $true

Write-Host ""
Write-Host "=== 2/3 发布：框架依赖 ==="
Publish-Variant -OutputDir (Join-Path $publishRoot "framework") -SelfContained $false

# ── 组装 ────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 3/3 打包 ==="

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$standaloneExe = Join-Path $stageDir $standaloneName
$frameworkExe = Join-Path $stageDir $frameworkName

Copy-Item (Join-Path $publishRoot "standalone/DesktopTwin.exe") $standaloneExe
Copy-Item (Join-Path $publishRoot "framework/DesktopTwin.exe") $frameworkExe

$readmeTemplate = Join-Path $PSScriptRoot "使用说明.txt"
if (-not (Test-Path $readmeTemplate)) {
    throw "缺少说明文件模板：$readmeTemplate"
}

# 说明文件与 CI 共用同一份模板，避免两边写的版本号/文件名对不上。
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

Write-Host ""
Write-Host "完成：$zipPath"
Write-Host ("  {0}：{1:N1} MB" -f $standaloneName, ((Get-Item $standaloneExe).Length / 1MB))
Write-Host ("  {0}：{1:N1} MB" -f $frameworkName, ((Get-Item $frameworkExe).Length / 1MB))
Write-Host "  压缩包：$zipSizeMb MB"
Write-Host "  SHA256：$hash"
