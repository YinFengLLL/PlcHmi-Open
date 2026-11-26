<#
.SYNOPSIS
    PlcHmi 提交前自检门禁（Windows / PowerShell 版）。

.DESCRIPTION
    与 tools/verify.sh 等价，但用 PowerShell 写 —— 本项目是纯 Windows 项目，
    让工程师为了跑门禁先去装 Git Bash，是门禁被绕过的最常见原因。

    任何一步失败即中止（$ErrorActionPreference = 'Stop'），红灯不许提交。
    配套文档：docs/04-代码审查标准与流程.md §5

.EXAMPLE
    pwsh tools/verify.ps1
#>

$ErrorActionPreference = 'Stop'

Set-Location (Join-Path $PSScriptRoot '..')
Write-Host "项目根目录：$(Get-Location)" -ForegroundColor Cyan
Write-Host

function Step([string] $Title) {
    Write-Host
    Write-Host "==> $Title" -ForegroundColor Cyan
}

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Step $Title
    & $Action

    if ($LASTEXITCODE -ne 0) {
        Write-Host
        Write-Host "门禁失败：$Title（退出码 $LASTEXITCODE）" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# ------------------------------------------------------------------ 1 构建
# --no-incremental 是必须的：增量编译会跳过未改动的项目，
# 于是"上游引入的警告"在热树上根本不重新报告 —— 门禁会假绿。
# 这个盲区真实发生过：同一棵树，带增量报 0 警告，强制重编报 CS0067。
#
# WarningsNotAsErrors=NU1900：NuGet 取漏洞数据失败是**网络**问题，不是代码问题。
# 不加这条，CI 抖一下网络就会以 "error" 名义把构建判红，且报错指不到任何代码。
# 门禁的职责是代码质量，不该被网络可用性绑架。
Invoke-Step '1/3 构建（要求 0 警告 0 错误）' {
    dotnet build PlcHmi.slnx -warnaserror --no-incremental -p:WarningsNotAsErrors=NU1900
}

# ------------------------------------------------------------------ 2 测试
Invoke-Step '2/3 单元 + 集成测试' {
    dotnet test PlcHmi.slnx --no-build
}

# ------------------------------------------------------- 2.5 版本号防回归
# 界面版本号曾硬编码成 v1.7.0，而程序集已经是 1.7.1 —— 现场报障时报的版本号
# 与日志/程序集对不上，排查要走弯路。现在版本号由程序集元数据绑定（见 主视图模型.版本号），
# 这里钉死"XAML 里不许再出现 v数字.数字 的字面量"，防止哪天又被手改回去。
Step '2.5/3 版本号不得硬编码在 XAML'
# 只匹配 Text 属性里的 "v数字.数字" 字面量，且大小写敏感 —— 否则图标路径里的
# "V6.9"（PathGeometry 的相对/绝对命令）会被误判成版本号，门禁天天假红。
# 变量一律用 ASCII：Windows PowerShell 5.1 以 ANSI 读取 ps1，中文变量名会乱码成非法标识符。
$versionLiterals = Get-ChildItem -Path 'src/PlcHmi.App' -Filter '*.xaml' -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern 'Text="v[0-9]+\.[0-9]+' -CaseSensitive -List
if ($versionLiterals) {
    Write-Host
    Write-Host 'XAML hardcoded version literal found. Bind 主视图模型.版本号 instead.' -ForegroundColor Red
    $versionLiterals | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }
    exit 1
}

# ------------------------------------------------------- 3 界面自检
Step '3/3 界面自检（11 页 + 8 对话框 + 交互）'

# 2026-10-12 现场要求 exe 改名「点胶锁付机」；旧名保留是为了兼容老的 publish/dist 目录
$smoke = @(
    'src/PlcHmi.App/bin/Debug/net10.0-windows/点胶锁付机.exe',
    'publish/点胶锁付机.exe',
    'src/PlcHmi.App/bin/Debug/net10.0-windows/PlcHmi.App.exe',
    'publish/PlcHmi.App.exe',
    'dist/PlcHmi.App.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $smoke) {
    Write-Host '   ⚠️  未找到「点胶锁付机.exe」，跳过界面自检' -ForegroundColor Yellow
    Write-Host '      请先执行：dotnet build PlcHmi.slnx'
}
else {
    Write-Host "   使用：$smoke"

    # WinExe（GUI 子系统）用 `& $exe` 调用时 PowerShell 不等待、也不设 $LASTEXITCODE，
    # 自检会"看起来跑了其实没看结果"（门禁假绿）。必须 Start-Process -Wait 取真实退出码。
    $selfCheck = Start-Process -FilePath $smoke -ArgumentList '--smoke' -Wait -PassThru

    if ($selfCheck.ExitCode -ne 0) {
        Write-Host
        Write-Host "门禁失败：界面自检有 $($selfCheck.ExitCode) 项失败" -ForegroundColor Red
        exit $selfCheck.ExitCode
    }
}

# ------------------------------------------------------- 4 静态门禁（可选）
# 注意顺序：py 优先，且排除 WindowsApps 下的 python 存根 ——
# Microsoft Store 的 python.exe 是个假命令，运行后无输出且退出码 0，
# 会让对比度/规模门禁"看起来跑了其实没跑"，门禁假绿。
$python = Get-Command py, python -ErrorAction SilentlyContinue |
    Where-Object { $_.Source -notmatch 'WindowsApps' } |
    Select-Object -First 1

if ($python) {
    foreach ($check in 'tools/check_contrast.py', 'tools/check_xaml_resources.py', 'tools/check_thresholds.py') {
        if (Test-Path $check) {
            Invoke-Step "4/4 $check" { & $python.Source $check }
        }
    }
}
else {
    Write-Host '   ⚠️  未找到 python，跳过对比度与规模门禁' -ForegroundColor Yellow
}

Write-Host
Write-Host '✅ 全部门禁通过，可以提交' -ForegroundColor Green
