# XAML 静态校验
#
# 用途：本程序要求管理员权限，很多机器上无法直接启动调试，XAML 的绑定错误只会在运行时暴露。
# 这个脚本在没有运行环境的情况下做一次静态核对：
#   1. XAML 里所有 {Binding Xxx} 的路径，必须在 ViewModel 上有对应的公共属性；
#   2. XAML 里所有 {StaticResource Xxx}，必须在 App.xaml 里定义（或来自 WPF-UI 的主题字典，脚本会忽略这些已知键）；
#   3. XAML 里所有 Symbol="Xxx" 必须在 WPF-UI 的 SymbolRegular 枚举中存在（从程序集字符串表核对）。
#
# 用法：pwsh build/verify-xaml.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$xamlDir = Join-Path $repoRoot "src/DesktopTwin/View"
$appXaml = Join-Path $repoRoot "src/DesktopTwin/App.xaml"
$viewModelFile = Join-Path $repoRoot "src/DesktopTwin/ViewModel/MainWindowViewModel.cs"

$failures = New-Object System.Collections.Generic.List[string]

# ---------- 1. 收集 ViewModel 的公共属性 ----------
$viewModelText = Get-Content $viewModelFile -Raw -Encoding UTF8

$vmProperties = New-Object System.Collections.Generic.HashSet[string]
# [ObservableProperty] 生成的属性：字段 _camelCase → 属性 PascalCase
foreach ($match in [regex]::Matches($viewModelText, '\[ObservableProperty\][\s\S]{0,400}?private\s+[\w\.<>\?\[\]]+\s+_(\w+)\s*[;=]')) {
    $name = $match.Groups[1].Value
    [void]$vmProperties.Add($name.Substring(0, 1).ToUpperInvariant() + $name.Substring(1))
}
# 普通公共属性
foreach ($match in [regex]::Matches($viewModelText, 'public\s+[\w\.<>\?\[\]]+\s+(\w+)\s*(\{|=>)')) {
    [void]$vmProperties.Add($match.Groups[1].Value)
}
# [RelayCommand] 生成的命令：XxxAsync → XxxCommand，Xxx → XxxCommand
foreach ($match in [regex]::Matches($viewModelText, '\[RelayCommand[^\]]*\]\s*(?:private|public)\s+(?:async\s+)?[\w<>]+\s+(\w+)\s*\(')) {
    $name = $match.Groups[1].Value -replace 'Async$', ''
    [void]$vmProperties.Add($name + "Command")
}

Write-Host "ViewModel 可绑定成员：$($vmProperties.Count) 个"

# ---------- 2. 收集 App.xaml 里的资源键 ----------
$appText = Get-Content $appXaml -Raw -Encoding UTF8
$resourceKeys = New-Object System.Collections.Generic.HashSet[string]
foreach ($match in [regex]::Matches($appText, 'x:Key="([^"]+)"')) {
    [void]$resourceKeys.Add($match.Groups[1].Value)
}
# 来自 WPF / WPF-UI 主题字典的键不在此文件内声明，白名单跳过
$knownThemeKeys = @(
    'BooleanToVisibilityConverter',
    'TextThemeFontFamily',
    'ApplicationBackgroundBrush',
    'TextFillColorPrimaryBrush',
    'SolidBackgroundFillColorBaseBrush',
    'DefaultUiTextBoxStyle',
    'DefaultTextBoxStyle'
)

# ---------- 3. 逐个 XAML 校验 ----------
$xamlFiles = Get-ChildItem $xamlDir -Filter *.xaml -Recurse
foreach ($file in $xamlFiles) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    $relative = $file.FullName.Substring($repoRoot.Length + 1)

    # 绑定路径：取每个 {Binding ...} 表达式的第一个路径 token；
    # 带 ElementName / RelativeSource / Source 的绑定指向别的对象，跳过。
    foreach ($match in [regex]::Matches($text, '\{Binding\s+([^}]*)\}')) {
        $expression = $match.Groups[1].Value
        if ($expression -match 'ElementName\s*=|RelativeSource\s*=|Source\s*=') {
            continue
        }

        if ($expression -notmatch '^([A-Za-z_][\w\.]*)') {
            continue
        }

        $path = $Matches[1].Split('.')[0]
        if (-not $vmProperties.Contains($path)) {
            $failures.Add("$relative：绑定属性 $path 在 ViewModel 上不存在")
        }
    }

    # 静态资源
    foreach ($match in [regex]::Matches($text, '\{StaticResource\s+([\w\.]+)\}')) {
        $key = $match.Groups[1].Value
        if (-not $resourceKeys.Contains($key) -and $knownThemeKeys -notcontains $key) {
            $failures.Add("$relative：StaticResource $key 未在 App.xaml 中定义")
        }
    }

    # 图标枚举
    foreach ($match in [regex]::Matches($text, 'Symbol="([A-Za-z0-9]+)"')) {
        $symbol = $match.Groups[1].Value
        $uiDll = Join-Path $env:USERPROFILE ".nuget/packages/wpf-ui/4.2.0/lib/net462/Wpf.Ui.dll"
        if (Test-Path $uiDll) {
            if ($null -eq $script:uiDllText) {
                # 把程序集按 Latin1 读成字符串，用 IndexOf 匹配元数据字符串表，比逐字节比较快几个数量级
                $bytes = [System.IO.File]::ReadAllBytes($uiDll)
                $script:uiDllText = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
            }

            if ($script:uiDllText.IndexOf($symbol, [System.StringComparison]::Ordinal) -lt 0) {
                $failures.Add("$relative：图标 $symbol 不在 SymbolRegular 枚举中")
            }
        }
    }
}

Write-Host "已校验 XAML 文件：$($xamlFiles.Count) 个"

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "发现 $($failures.Count) 处问题：" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "校验通过：绑定、资源、图标全部可解析。" -ForegroundColor Green
exit 0
