# 本地打包脚本
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build/build.ps1
#
# 产出三个文件（不压缩）：
#   桌面分身-<版本>.exe        自带 .NET 运行时，开箱即用
#   桌面分身-<版本>-NET10.exe  框架依赖，需先装 .NET 10 桌面运行时
#   使用说明.txt               两个版本的说明与各自的 SHA256
#
# 输出目录：artifacts/dist/

[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/DesktopTwin/DesktopTwin.csproj"
$artifactsDir = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactsDir "publish"
$distDir = Join-Path $artifactsDir "dist"

Write-Host "仓库根目录：$repoRoot"

$version = (Get-Item $project | Select-String -Pattern "<Version>([^<]+)</Version>").Matches[0].Groups[1].Value
if (-not $version) {
    $version = "0.0.0"
}

# 文件名直接用中文：用户在下载列表里看到的就是「桌面分身-0.2.2.exe」，
# 不用先解压再猜哪个是哪个。程序内部不依赖自身文件名，
# 改名不影响单实例互斥体、设置路径和图标（都按程序集名走）。
$standaloneName = "桌面分身-$version.exe"
$frameworkName = "桌面分身-$version-NET10.exe"

foreach ($dir in @($publishRoot, $distDir)) {
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
Write-Host "=== 3/3 输出到 artifacts/dist ==="

New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$standaloneExe = Join-Path $distDir $standaloneName
$frameworkExe = Join-Path $distDir $frameworkName

Copy-Item (Join-Path $publishRoot "standalone/DesktopTwin.exe") $standaloneExe
Copy-Item (Join-Path $publishRoot "framework/DesktopTwin.exe") $frameworkExe

$readmeTemplate = Join-Path $PSScriptRoot "使用说明.txt"
if (-not (Test-Path $readmeTemplate)) {
    throw "缺少说明文件模板：$readmeTemplate"
}

# 两个 exe 名字相近，只给压缩包的哈希没法分别校验，
# 所以把每个文件的 SHA256 直接写进说明里。
$standaloneHash = (Get-FileHash $standaloneExe -Algorithm SHA256).Hash
$frameworkHash = (Get-FileHash $frameworkExe -Algorithm SHA256).Hash

# 说明文件与 CI 共用同一份模板，避免两边写的版本号/文件名对不上。
$readme = (Get-Content $readmeTemplate -Raw -Encoding UTF8).
    Replace("{{VERSION}}", $version).
    Replace("{{SHA256_STANDALONE}}", $standaloneHash).
    Replace("{{SHA256_FRAMEWORK}}", $frameworkHash)

# 用 UTF-8 带 BOM 写：记事本在旧版本 Windows 上靠 BOM 判断编码，
# 没有 BOM 时中文会显示成乱码。
$readmePath = Join-Path $distDir "使用说明.txt"
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))

Write-Host ""
Write-Host "完成：$distDir"
Write-Host ("  {0}" -f $standaloneName)
Write-Host ("    {0,7:N1} MB   {1}" -f ((Get-Item $standaloneExe).Length / 1MB), $standaloneHash)
Write-Host ("  {0}" -f $frameworkName)
Write-Host ("    {0,7:N1} MB   {1}" -f ((Get-Item $frameworkExe).Length / 1MB), $frameworkHash)
Write-Host ("  使用说明.txt")
Write-Host ("    {0,7:N1} KB   {1}" -f ((Get-Item $readmePath).Length / 1KB), (Get-FileHash $readmePath -Algorithm SHA256).Hash)
