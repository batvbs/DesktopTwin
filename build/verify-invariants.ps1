# 校验那些类型系统表达不了的约束。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File build/verify-invariants.ps1
#
# 目前只有一类：安全常量必须只有一个定义处。
# 这类约束编译器管不到——把 CLSID 复制一份到别处，代码照样编译、测试照样绿，
# 但改的时候漏一处就会出现「检查的是 A、报给用户的是 B」，两边都不报错。
#
# 刻意按原始文本计数，不跳过注释行：注释里写一句「路径形如 Software\Classes\CLSID」
# 同样会被判为重复。这是有意的取舍——误报会立刻被看到、改个措辞就过去，
# 漏报则完全无声。需要举例时请写成不带完整路径的形式。
# 将来若误报真的造成困扰，再改成跳过注释行；在那之前保持严格。

[CmdletBinding()]
param(
    # 仅供反向验证脚本本身使用，默认校验真实源文件。
    [string]$Path
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$launcherPath = if ($Path) { $Path } else { Join-Path $repoRoot "src/DesktopTwin/Services/ChildSessionProcessLauncher.cs" }

if (-not (Test-Path $launcherPath)) {
    throw "找不到 $launcherPath"
}

# -Encoding UTF8 在 Windows PowerShell 5.1 下会正确识别 BOM；源文件本身是 UTF-8。
$content = Get-Content $launcherPath -Raw -Encoding UTF8
$failures = @()

$invariants = @(
    @{
        Name    = "HKCU COM 注册根路径"
        Pattern = 'Software\Classes'
        Hint    = "请改用 ChildSessionProcessLauncher.UserClassesPrefix 拼接，不要再写字面量。"
    },
    @{
        Name    = "任务计划程序 CLSID"
        Pattern = '0f87369f-a4e5-4cfc-bd3e-73e6154572dd'
        Hint    = "请改用 ChildSessionProcessLauncher.TaskSchedulerClsid，不要再写字面量。"
    }
)

foreach ($item in $invariants) {
    $count = ([regex]::Matches($content, [regex]::Escape($item.Pattern))).Count
    if ($count -ne 1) {
        $failures += "$($item.Name)（$($item.Pattern)）出现了 $count 次，应当只出现 1 次。$($item.Hint)"
    }
}

if ($failures.Count -gt 0) {
    Write-Host "常量单一来源校验未通过：" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host "常量单一来源校验通过：$($invariants.Count) 项均为唯一定义。"
