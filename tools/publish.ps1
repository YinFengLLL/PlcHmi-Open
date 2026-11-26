<#
.SYNOPSIS
    点胶锁付机上位机正式发布：生成免安装的 publish/ 目录。

.DESCRIPTION
    发布形态按现场需要定为「文件夹 + 免安装」：
    现场工程师要能直接打开 config/*.json 改点位，单文件 exe 反而挡路。

    包结构（根目录只留必要的东西）：

        点胶锁付机.exe          程序入口
        *.dll / zh-Hans\        运行时（卫星语言只保留中文，其余语言目录不出包）
        出厂配置\               出厂配置模板 —— <b>不是</b> config/
        说明.txt                首次部署 / 覆盖升级须知
        logs\ data\             运行时生成，不出包

    <b>为什么带「出厂配置」而不带 config/：</b>现场升级就是"把新版本目录覆盖到原程序目录"。
    若发布包里直接带 config/，覆盖时会把现场调好的机种（recipes.json）、PLC 连接参数
    （appsettings.json）、账号、点位表一并冲掉——那是生产事故。
    现在包里只有模板，程序启动时只补齐缺失的文件、绝不覆盖已存在的
    （见 配置路径.补齐缺失配置），覆盖升级因此天然安全。

    同时清理这些不该出现在发布包里的东西：
      · *.bak / *.tmp —— 运行时原子写的中间产物，属于开发机残留；
      · logs/ / data/ —— 运行时目录，留着会把上一台机器的报警历史带过去；
      · *.pdb         —— 调试符号，现场用不到且暴露内部结构；
      · createdump.exe —— .NET 崩溃转储工具，现场不需要；
      · cs/ de/ es/ … —— 非中文卫星语言目录（csproj 里 SatelliteResourceLanguages 已裁剪）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -Output D:\release\点胶锁付机
#>

param(
    [string] $Output = 'publish',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# exe 与主 DLL 的名字（AssemblyName 改了这里要跟着改）
$程序名 = '点胶锁付机'

Set-Location (Join-Path $PSScriptRoot '..')

$outPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Output))

# 2026-11-26 开源版：仓库不再携带公司 Logo，发布流程也不生成图标（exe 用系统默认图标）。

# ------------------------------------------------------------------ 1 清理 + 发布
Write-Host "==> 清理旧产物：$outPath" -ForegroundColor Cyan
if (Test-Path $outPath) {
    Remove-Item $outPath -Recurse -Force
}

Write-Host "==> 发布（自包含 win-x64，工控机免装 .NET）" -ForegroundColor Cyan
# -m:1：发布是一次性动作，不在乎并行省下的几秒；单节点可以避开受限环境
#（沙箱 / 某些 CI）里 MSBuild 工作节点起不来所导致的"0 错误却生成失败"。
dotnet publish src/PlcHmi.App `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -m:1 `
    -o $outPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

Write-Host "==> 清理发布包" -ForegroundColor Cyan

<#
    清理必须在两处调用：发布后一次、自检后一次。
    自检（--smoke）会真实启动一次程序，于是又把 logs/ 写了回来 ——
    只在自检前清理，交付包里就会躺着一份"发布机自检日志"，
    现场看到会以为程序已经有运行记录了。
#>
function Clear-PackageArtifacts([string] $Path) {
    # 原子写的中间产物：现场第一次保存时程序会自己生成，不需要随包发出
    Get-ChildItem -Path $Path -Recurse -File -Include *.bak, *.tmp -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "    删除 $($_.Name)"; Remove-Item $_.FullName -Force }

    # 运行时目录：留着会把上一台机器的数据带过去
    foreach ($runtimeDir in 'logs', 'data') {
        $p = Join-Path $Path $runtimeDir
        if (Test-Path $p) { Write-Host "    删除 $runtimeDir/"; Remove-Item $p -Recurse -Force }
    }

    # 调试符号
    Get-ChildItem -Path $Path -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force }

    # .NET 崩溃转储工具：现场用不到
    $dump = Join-Path $Path 'createdump.exe'
    if (Test-Path $dump) { Write-Host '    删除 createdump.exe'; Remove-Item $dump -Force }

    # 非中文卫星语言目录（csproj 已裁剪，这里兜底删掉历史产物里残留的）
    foreach ($dir in (Get-ChildItem -Path $Path -Directory)) {
        if ($dir.Name -in 'zh-Hans', '出厂配置', '部署') { continue }

        # 语言目录的共同特征：里面全是 .dll 且目录名是语言代码
        $全部为资源 = (Get-ChildItem $dir.FullName -File | Where-Object { $_.Extension -ne '.dll' }).Count -eq 0
        if ($全部为资源 -and $dir.Name.Length -le 6) {
            Write-Host "    删除语言目录 $($dir.Name)/"
            Remove-Item $dir.FullName -Recurse -Force
        }
    }
}

Clear-PackageArtifacts $outPath

# ------------------------------------------------- 2 config → 出厂配置
#
# 顺序很关键：
#   · 自检（--smoke）之前改名 —— 自检跑的就是"包里没有 config/，启动时从出厂配置补齐"这条路径；
#   · 自检会真的把 config/ 补出来 → 自检后要把这份 config/ 删掉，包里只留 出厂配置/。
Write-Host "==> 整理为出厂配置模式（包里不带 config/）" -ForegroundColor Cyan

$configDir = Join-Path $outPath 'config'
$出厂目录 = Join-Path $outPath '出厂配置'

if (Test-Path $出厂目录) {
    Remove-Item $出厂目录 -Recurse -Force
}

if (-not (Test-Path $configDir)) {
    throw "发布包里没有 config/ —— 无法整理出厂配置"
}

Move-Item $configDir $出厂目录
Write-Host "    config/ → 出厂配置/（程序启动时只补齐缺失文件，绝不覆盖现场已改的配置）"

# ------------------------------------------------------------------ 3 核对
Write-Host ""
Write-Host "==> 发布核对" -ForegroundColor Cyan

$出厂文件 = Get-ChildItem $出厂目录 -File -Filter *.json
Write-Host "    出厂配置/ 模板：$($出厂文件.Count) 个"
$出厂文件 | ForEach-Object { Write-Host "      $($_.Name)" }

if (Test-Path (Join-Path $outPath 'config')) {
    throw "发布包里不应出现 config/（那会覆盖现场配置）—— 请检查整理步骤"
}

$exe = Join-Path $outPath "$程序名.exe"
if (-not (Test-Path $exe)) {
    throw "发布包里没有 $程序名.exe"
}

$size = (Get-ChildItem $outPath -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("    产物：{0}" -f $exe)
Write-Host ("    总大小：{0:N1} MB" -f ($size / 1MB))
Write-Host ("    可执行文件：{0:N0} KB（apphost；运行时在同目录的 DLL 里）" -f ((Get-Item $exe).Length / 1KB))

if ($出厂文件.Count -lt 10) {
    Write-Warning "出厂配置/ 只有 $($出厂文件.Count) 个 JSON，预期 11 个 —— 请确认是否完整"
}

# ------------------------------------------------------------------ 4 部署说明
$说明 = @"
点胶锁付机上位机 · 部署与升级说明
======================================

【首次部署（新机台）】
1. 把本目录整个拷贝到目标机的任意目录（例如 D:\点胶锁付机），目录必须可写；
2. 双击 点胶锁付机.exe 即可运行 —— 免安装 .NET（自包含）；
3. 首次启动会自动把「出厂配置\」里的模板补齐到 config\（只补缺失，不覆盖已有）；
4. 管理员登录 → 系统设置 → PLC 连接，改成现场 PLC 的 IP 后重启生效。

【覆盖升级（已有旧版本的机台）】
1. 直接把本目录的内容覆盖到原程序目录即可；
   —— 本包里没有 config\，只有 出厂配置\ 模板，
      所以覆盖【不会】碰到现场已调好的任何配置。对照如下：

        现场改过的东西                       存放文件（config\）
        ----------------------------------  ---------------------------
        机种与点表数据（D2500–D3500 快照）    recipes.json
        全局参数（轴速度等，机种区外）        parameters.json
        PLC 连接参数（IP / 端口 / 协议模式）  appsettings.json
        报警条目（新增 / 改文本 / 改地址）    alarms.json
        IO 点位与名称（新增栏 / 改名）        io-points.json
        手动页元件（气缸/按钮/指示灯 + 地址） manual-widgets.json
        运行监控页元件地址                   tags.json
        参数地址 / 轴地址 / 点表列基址        recipe-config.json
        历史记录数据体（触发位 + 各列）       history-record.json
        汇川变量表                           variables.json
        其它点（辅助点位）定义               aux-points.json
        延时表 / 自定义按钮                  param-tables.json
        账号与角色                           users.json

   —— 新版本新增的配置文件会在下次启动时自动补进 config\；
   注意：已有文件【一个字都不会被改】。若某个新增条目（例如新版自带的报警/元件）
   需要进入一台已有配置的机台，请在机台上用界面新增（报警页/手动页/IO 页都支持），
   或由工程师用 Excel/文本对照合并对应 JSON —— 这是刻意设计：
   自动覆盖文件是丢现场数据的最常见原因。
2. 覆盖前建议先关掉正在运行的程序（任务栏右键退出）；
3. 覆盖后首次启动，日志里会出现
   「已从出厂配置补齐 N 个缺失的配置文件」—— 只列新增项，不列被保留的项。

【保险建议】
升级前把整个 config\ 目录拷一份到别处（例如 D:\config-备份-日期）。
程序自身也会在写配置时生成 .bak 备份，损坏时启动会自动回退。

【日常提示】
· 顶栏橙色条「PLC 未连接，数据未刷新」= 通信没连上（网线/IP），连接恢复后自动消失；
· 顶栏红色条「程序异常…」= 程序缺陷，请拍照并反馈，logs\plchmi-日期.log 里有详情；
· logs\ 与 data\ 是运行数据（报警历史、机种之外的运行记录），不要从别的机器拷过来。
"@

[System.IO.File]::WriteAllText(
    (Join-Path $outPath '说明.txt'),
    $说明,
    (New-Object System.Text.UTF8Encoding($true)))
Write-Host "    已生成 说明.txt（首次部署 / 覆盖升级须知）"

# ------------------------------------------------------------------ 5 自检
Write-Host ""
Write-Host "==> 自检（--smoke，验证「无 config/ 时从出厂配置补齐」路径）" -ForegroundColor Cyan

<#
    必须 Start-Process -Wait：本程序是 WinExe（GUI 子系统），用 `& $exe` 调用时
    PowerShell **不会等待**，`$LASTEXITCODE` 也不会被它更新 ——
    自检就变成了"启动了但没看结果"，而且后续清理会和仍在运行的进程抢文件
    （logs/ 会被它写回来，交付包里就多出一份自检日志）。
#>
$selfCheck = Start-Process -FilePath $exe -ArgumentList '--smoke' `
    -WorkingDirectory $outPath -Wait -PassThru

if ($selfCheck.ExitCode -ne 0) {
    throw "发布产物自检失败，退出码 $($selfCheck.ExitCode)"
}

Write-Host "    自检通过（config/ 已由出厂配置自动补齐）"

# 自检会真实启动程序 → 清理它写回的 logs/ 与 config/
Write-Host "==> 清理自检产生的运行时文件" -ForegroundColor Cyan
Clear-PackageArtifacts $outPath

$自检生成的配置 = Join-Path $outPath 'config'
if (Test-Path $自检生成的配置) {
    Write-Host '    删除 config/（自检时由出厂配置补齐产生，交付包里只留出厂配置/）'
    Remove-Item $自检生成的配置 -Recurse -Force
}

if (-not (Test-Path $出厂目录)) {
    throw "出厂配置/ 不见了 —— 清理步骤有误"
}

Write-Host ""
Write-Host "✅ 发布完成：$outPath" -ForegroundColor Green
Write-Host "   部署：整个目录拷到目标机即可，无需安装 .NET。"
Write-Host "   覆盖升级不会丢现场配置（包里只有出厂配置模板，详见 说明.txt）。"
Write-Host "   注意：目标目录必须可写（配置与报警历史写在程序目录下）。"
