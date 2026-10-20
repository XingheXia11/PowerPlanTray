# setup_plans.ps1 —— 在任意 Windows 10/11 电脑上创建同款三档电源计划
# 电影模式（安静观影）/ AI模式（开发长任务）/ 游戏模式（CS2 等竞技游戏）
#
# 本轮策略（长期插电使用）：
#   - 只写交流电(AC)参数，不主动覆盖直流电(DC)：新复制的计划 DC 沿用“平衡”母本，已有计划保留原 DC
#   - 每项写完回读校验：明确显示 AC 期望值与实际值，并核对 DC 未被本脚本改动
#   - 本机不存在的设置打印“本机不存在”，不当成成功，也不因此中断整个脚本
#   - 逐项检查 powercfg 退出码；任一失败/警告汇总在最后统一列出
#   - 幂等：已存在同名计划直接复用刷新，不重复创建；不切换你当前正在使用的计划
#
# 注意：下列三档参数是“首轮评估基线”，来自特定笔记本环境，不是对所有 CPU/BIOS/散热都最优的终值。
#       处理器代际、厂商 BIOS、电源管理模式、散热设计与 Windows 版本都会影响实际效果，需实机验证
#       （对比任务耗时、频率/温度趋势、噪声或帧率）后再决定是否调整，切勿仅凭参数名称推断性能。
#
# 运行：powershell -ExecutionPolicy Bypass -File setup_plans.ps1

$ErrorActionPreference = 'Continue'

$BALANCED_GUID = '381b4222-f694-41f0-9685-ff5bb260df2e' # 所有 Windows 都自带的“平衡”计划，用作复制母本

$SUB_PROC  = '54533251-82be-4824-96c1-47b60b740d00'
$SUB_USB   = '2a737441-1930-4402-8d77-b2bebba308a3'
$SUB_PCIE  = '501a4d13-42af-4429-9fd1-a8218c268e20'
$SUB_SLEEP = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$SUB_DISK  = '0012ee47-9041-4b5d-9b77-535fba8b1442'
$SUB_VIDEO = '7516b95f-f776-4464-8c53-06167f40cc99' # 显示设置子组（显示器关闭超时）
$SUB_IGFX  = '44f3beca-a7c0-460e-9df2-bb8b99e0cba6' # Intel 核显驱动注册的子组，其他机器上不存在

$MIN        = '893dee8e-2bef-41e0-89c6-b55d0929964c'
$MAX        = 'bc5038f7-23e0-4960-96da-33abaf5935ec'
$BOOST      = 'be337238-0d82-4146-a960-4f3749d470c7'
$PARKMIN    = '0cc5b647-c1df-4637-891a-dec35c318583'
$COOLING    = '94d3a615-a899-4ac5-ae2b-e4d8f634367f'
$USBSUSPEND = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
$PCIELINK   = 'ee12f906-d277-404b-b6da-e5fa1a576df5'
$SLEEPIDLE  = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'
$HIBERNATE  = '9d7815a6-7ee4-497e-8888-515a05f02364'
$WAKETIMER  = 'bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d'
$DISKIDLE   = '6738e2c4-e8a5-4a42-b16a-e040e769756e'
$VIDEOIDLE  = '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e' # 关闭显示器超时（秒），与系统睡眠分开
$IGFXPOWER  = '3619c3f2-afb2-4afc-b0e9-e7fef372de36'

# 交流电(AC)显示器关闭超时（分钟）。与系统睡眠分开：屏幕可关，后台长任务继续运行。
# 设为 0 表示不写入该项（保留继承值）。建议 10~15 分钟。
$DisplayOffMinAC = 15

# 三档参数（首轮评估基线，需实机验证；$null 表示不写入、沿用继承/系统默认值）
#   Min/Max：最小/最大处理器状态(%)。Max=99% 常被用作限制睿频的兼容手段，但各平台表现不一定相同
#   Boost：睿频模式，0=Disabled 1=Enabled 2=Aggressive（语义以微软 PERFBOOSTMODE 文档为准，平台适用性不同）
#   Park：核心停驻最小核心数(%)。三档默认全部保留系统/“平衡”默认行为，不强制 100；
#         仅当高性能档实测确有收益时再考虑显式设值
#   Cool：散热策略 0=被动 1=主动；Usb：USB 选择性暂停 0=关 1=开；Pcie：链路省电 0=关 1=中 2=最大
#   Igfx：核显电源计划 0=省电 1=平衡 2=最高性能（独显直连机器无此项，自动跳过）
#   Sleep/Hib/Disk：AC 睡眠/休眠/硬盘停转超时(秒)，0=从不，避免长任务被空闲超时打断（保留既有行为）
#   Wake：唤醒定时器 0=禁用（沿用既有配置，本轮不改动合盖动作等未纳入范围的设置）
$dispSec = $DisplayOffMinAC * 60
$Modes = @(
    @{ Name = '电影模式'; Desc = '观影模式(基线,需实机验证)：AC 限制睿频、安静散热、观影中不睡眠';
       Min = 5;  Max = 99;  Boost = 0; Park = $null; Cool = 1; Usb = 1; Pcie = 1; Igfx = 1; Sleep = 0; Hib = 0; Wake = 0; Disk = 0; Display = $dispSec },
    @{ Name = 'AI模式';   Desc = 'AI 开发模式(基线,需实机验证)：AC 限制睿频降发热、长任务不睡眠';
       Min = 5;  Max = 99;  Boost = 0; Park = $null; Cool = 0; Usb = 0; Pcie = 0; Igfx = 1; Sleep = 0; Hib = 0; Wake = 0; Disk = 0; Display = $dispSec },
    @{ Name = '游戏模式'; Desc = '游戏模式(基线,需实机验证)：AC 睿频全开、峰值性能、游戏中不睡眠';
       Min = 5;  Max = 100; Boost = 2; Park = $null; Cool = 0; Usb = 0; Pcie = 0; Igfx = 2; Sleep = 0; Hib = 0; Wake = 0; Disk = 0; Display = $dispSec }
)

function Get-PlanIdByName([string]$name) {
    $list = powercfg /list 2>$null | Out-String
    foreach ($line in ($list -split "`n")) {
        if ($line -like "*$name*" -and $line -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
            return $Matches[1]
        }
    }
    return $null
}

# 回读单个设置的交流/直流当前值。单设置查询输出里最后两个十六进制值固定是 交流、直流，
# 与系统显示语言无关（范围型设置前面的“最小/最大/增量”行在它之前，值列型设置没有别的十六进制行）。
function Get-Values([string]$id, [string]$sub, [string]$setting) {
    $txt = (powercfg /query $id $sub $setting 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0) { return $null }
    $hex = [regex]::Matches($txt, '0x[0-9a-fA-F]{8}')
    if ($hex.Count -lt 2) { return $null }
    $n = $hex.Count
    return @([convert]::ToInt32($hex[$n - 2].Value, 16), [convert]::ToInt32($hex[$n - 1].Value, 16))
}

# 只写 AC；写完回读校验 AC 是否达到期望值，并核对 DC 未被本脚本改动
$script:Failures = @()
function Set-AC([string]$id, [string]$sub, [string]$setting, [string]$label, [int]$value) {
    $before = Get-Values $id $sub $setting
    if ($null -eq $before) { Write-Host ("    本机不存在: " + $label); return }
    $dcBefore = $before[1]

    powercfg /setacvalueindex $id $sub $setting $value 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ("    写入失败(exit=" + $LASTEXITCODE + "): " + $label)
        $script:Failures += ($label + "：powercfg 写入返回退出码 " + $LASTEXITCODE)
        return
    }

    $after = Get-Values $id $sub $setting
    if ($null -eq $after) {
        Write-Host ("    回读失败: " + $label)
        $script:Failures += ($label + "：写入后无法回读校验")
        return
    }

    $acOk = ($after[0] -eq $value)
    $dcChanged = ($after[1] -ne $dcBefore)
    if ($acOk -and -not $dcChanged) {
        Write-Host ("    已生效(AC)  " + $label + " = " + $value + "   [DC 保持 " + $after[1] + "，未改动]")
    } elseif ($acOk -and $dcChanged) {
        Write-Host ("    警告: " + $label + " AC=" + $value + "，但 DC 意外从 " + $dcBefore + " 变为 " + $after[1])
        $script:Failures += ($label + "：DC 值被意外修改（" + $dcBefore + " -> " + $after[1] + "）")
    } else {
        Write-Host ("    未生效(AC)  " + $label + " 期望 " + $value + "，实际 AC=" + $after[0])
        $script:Failures += ($label + "：回读 AC=" + $after[0] + " 与期望 " + $value + " 不符")
    }
}

# $null 表示保留继承/默认值，不写入
function Apply-AC([string]$id, [string]$sub, [string]$setting, [string]$label, $value) {
    if ($null -eq $value) {
        Write-Host ("    保留默认: " + $label + "（不写入，沿用继承值）")
        return
    }
    Set-AC $id $sub $setting $label ([int]$value)
}

$touched = @()

foreach ($m in $Modes) {
    Write-Host ("== " + $m.Name + " ==")
    $id = Get-PlanIdByName $m.Name

    if (-not $id) {
        # 从自带的“平衡”计划复制一份（所有 Windows 10/11 都存在）；DC 参数随之继承自“平衡”
        $raw = powercfg /duplicatescheme $BALANCED_GUID 2>$null | Out-String
        if ($raw -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
            $id = $Matches[1]
            Write-Host ("    已创建新计划: " + $id)
        } else {
            Write-Host "    创建失败，跳过该模式"
            $script:Failures += ($m.Name + "：从平衡计划复制失败")
            continue
        }
    } else {
        Write-Host ("    已存在，复用并刷新参数: " + $id)
    }

    $touched += $id
    powercfg /changename $id $m.Name $m.Desc
    Apply-AC $id $SUB_PROC  $MIN        '最小处理器状态'     $m.Min
    Apply-AC $id $SUB_PROC  $MAX        '最大处理器状态'     $m.Max
    Apply-AC $id $SUB_PROC  $BOOST      '睿频模式'           $m.Boost
    Apply-AC $id $SUB_PROC  $PARKMIN    '核心停驻最小核心数' $m.Park
    Apply-AC $id $SUB_PROC  $COOLING    '散热策略'           $m.Cool
    Apply-AC $id $SUB_USB   $USBSUSPEND 'USB选择性暂停'      $m.Usb
    Apply-AC $id $SUB_PCIE  $PCIELINK   'PCIe链路省电'       $m.Pcie
    Apply-AC $id $SUB_IGFX  $IGFXPOWER  '核显电源计划'       $m.Igfx
    Apply-AC $id $SUB_SLEEP $SLEEPIDLE  '睡眠超时'           $m.Sleep
    Apply-AC $id $SUB_SLEEP $HIBERNATE  '休眠超时'           $m.Hib
    Apply-AC $id $SUB_SLEEP $WAKETIMER  '允许唤醒定时器'     $m.Wake
    Apply-AC $id $SUB_DISK  $DISKIDLE   '关闭硬盘超时'       $m.Disk
    Apply-AC $id $SUB_VIDEO $VIDEOIDLE  '关闭显示器超时(AC)' $m.Display
}

# 改的正是当前激活方案时，重新应用一次，新值才会立刻落到硬件上（不会切换到其他方案）
$activeLine = powercfg /getactivescheme 2>$null | Out-String
if ($activeLine -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
    $active = $Matches[1]
    if ($touched -contains $active) {
        powercfg /setactive $active 2>$null
        Write-Host ''
        Write-Host '当前激活的方案已被重新应用，新值立即生效（未切换方案）。'
    }
}

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host ('完成，但存在 ' + $script:Failures.Count + ' 项失败/警告：')
    foreach ($f in $script:Failures) { Write-Host ('  - ' + $f) }
} else {
    Write-Host '全部完成：所有受支持的 AC 设置均已写入并回读校验通过；DC 值未被本脚本修改。'
}

Write-Host ''
Write-Host '当前电源计划列表：'
powercfg /list
