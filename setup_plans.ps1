# setup_plans.ps1 —— 在任意 Windows 10/11 电脑上创建同款三档电源计划
# 电影模式（安静观影）/ AI模式（开发长任务）/ 游戏模式（CS2 等竞技游戏）
#
# 特点：
#   - 幂等：已存在同名计划时直接复用并刷新参数，不会重复创建
#   - 不需要管理员权限（电源计划是按用户存储的）
#   - 不切换当前正在使用的计划，只创建/更新
#   - 每项写完回读校验：本机没有的设置会明确打印"本机不存在"，不静默跳过
#
# 运行：powershell -ExecutionPolicy Bypass -File setup_plans.ps1

$ErrorActionPreference = 'Continue'

$BALANCED_GUID = '381b4222-f694-41f0-9685-ff5bb260df2e' # 所有 Windows 都自带的“平衡”计划，用作复制母本

$SUB_PROC  = '54533251-82be-4824-96c1-47b60b740d00'
$SUB_USB   = '2a737441-1930-4402-8d77-b2bebba308a3'
$SUB_PCIE  = '501a4d13-42af-4429-9fd1-a8218c268e20'
$SUB_SLEEP = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$SUB_DISK  = '0012ee47-9041-4b5d-9b77-535fba8b1442'
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
$IGFXPOWER  = '3619c3f2-afb2-4afc-b0e9-e7fef372de36'

# 三档参数（笔记本 i7-12700H 实测调优；台式机同样适用）
#   Max=99% 表示关闭睿频：视频/开发场景安静低温，游戏保留 100% 睿频
#   Pcie 链路省电：0=关闭 1=中等电源节省 2=最大电源节省（性能档必须 0，2 会带来 DPC 延迟尖峰）
#   Igfx 核显电源计划：0=最省电 1=平衡 2=最高性能（独显直连机器上此项无意义，会自动跳过）
#   Hib/Wake/Disk：休眠超时、唤醒定时器、硬盘停转，全部归零，避免通宵任务被打断
$Modes = @(
    @{ Name = '电影模式'; Desc = '观影模式：关睿频、安静散热、观影中不休眠';
       Min = 5;  Max = 99;  Boost = 0; Park = 100; Cool = 1; Usb = 1; Pcie = 1; Igfx = 1; Sleep = 0; Hib = 0; Wake = 0; Disk = 0 },
    @{ Name = 'AI模式';   Desc = 'AI 开发模式：关睿频降发热、USB/PCIe省电关、永不休眠，适合训练与推理长任务';
       Min = 20; Max = 99;  Boost = 0; Park = 100; Cool = 0; Usb = 0; Pcie = 0; Igfx = 1; Sleep = 0; Hib = 0; Wake = 0; Disk = 0 },
    @{ Name = '游戏模式'; Desc = '游戏模式：睿频全开、核心不停驻、PCIe/USB省电关、永不休眠；最小状态由系统动态调频防过热';
       Min = 5;  Max = 100; Boost = 2; Park = 100; Cool = 0; Usb = 0; Pcie = 0; Igfx = 2; Sleep = 0; Hib = 0; Wake = 0; Disk = 0 }
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
# 与系统显示语言无关（范围型设置前面的"最小/最大/增量"行在它之前，值列型设置没有别的十六进制行）。
function Get-Both([string]$id, [string]$sub, [string]$setting) {
    $txt = (powercfg /query $id $sub $setting 2>$null | Out-String)
    if ($LASTEXITCODE -ne 0) { return $null }
    $hex = [regex]::Matches($txt, '0x[0-9a-fA-F]{8}')
    if ($hex.Count -lt 2) { return $null }
    $n = $hex.Count
    return @([convert]::ToInt32($hex[$n - 2].Value, 16), [convert]::ToInt32($hex[$n - 1].Value, 16))
}

function Set-Both([string]$id, [string]$sub, [string]$setting, [string]$label, [int]$value) {
    if ($null -eq (Get-Both $id $sub $setting)) { Write-Host ("    本机不存在: " + $label); return }
    powercfg /setacvalueindex $id $sub $setting $value 2>$null
    powercfg /setdcvalueindex $id $sub $setting $value 2>$null
    $after = Get-Both $id $sub $setting
    if ($after[0] -eq $value -and $after[1] -eq $value) {
        Write-Host ("    已生效  " + $label + " = " + $value)
    } else {
        Write-Host ("    未生效  " + $label + " 期望 " + $value + "，实际 交流=" + $after[0] + " 直流=" + $after[1])
    }
}

$touched = @()

foreach ($m in $Modes) {
    Write-Host ("== " + $m.Name + " ==")
    $id = Get-PlanIdByName $m.Name

    if (-not $id) {
        # 从自带的“平衡”计划复制一份（所有 Windows 10/11 都存在）
        $raw = powercfg /duplicatescheme $BALANCED_GUID 2>$null | Out-String
        if ($raw -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
            $id = $Matches[1]
            Write-Host ("    已创建新计划: " + $id)
        } else {
            Write-Host "    创建失败，跳过该模式"
            continue
        }
    } else {
        Write-Host ("    已存在，复用并刷新参数: " + $id)
    }

    $touched += $id
    powercfg /changename $id $m.Name $m.Desc
    Set-Both $id $SUB_PROC  $MIN        '最小处理器状态'     $m.Min
    Set-Both $id $SUB_PROC  $MAX        '最大处理器状态'     $m.Max
    Set-Both $id $SUB_PROC  $BOOST      '睿频模式'           $m.Boost
    Set-Both $id $SUB_PROC  $PARKMIN    '核心停驻最小核心数' $m.Park
    Set-Both $id $SUB_PROC  $COOLING    '散热策略'           $m.Cool
    Set-Both $id $SUB_USB   $USBSUSPEND 'USB选择性暂停'      $m.Usb
    Set-Both $id $SUB_PCIE  $PCIELINK   'PCIe链路省电'       $m.Pcie
    Set-Both $id $SUB_IGFX  $IGFXPOWER  '核显电源计划'       $m.Igfx
    Set-Both $id $SUB_SLEEP $SLEEPIDLE  '睡眠超时'           $m.Sleep
    Set-Both $id $SUB_SLEEP $HIBERNATE  '休眠超时'           $m.Hib
    Set-Both $id $SUB_SLEEP $WAKETIMER  '允许唤醒定时器'     $m.Wake
    Set-Both $id $SUB_DISK  $DISKIDLE   '关闭硬盘超时'       $m.Disk
}

# 改的正是当前激活方案时，重新应用一次，新值才会立刻落到硬件上
$activeLine = powercfg /getactivescheme 2>$null | Out-String
if ($activeLine -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})') {
    $active = $Matches[1]
    if ($touched -contains $active) {
        powercfg /setactive $active 2>$null
        Write-Host ''
        Write-Host '当前激活的方案已被重新应用，新值立即生效（不会切换方案）。'
    }
}

Write-Host ''
Write-Host '全部完成。当前电源计划列表：'
powercfg /list
