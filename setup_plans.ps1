# setup_plans.ps1 —— 在任意 Windows 10/11 电脑上创建同款三档电源计划
# 电影模式（安静观影）/ AI模式（开发长任务）/ 游戏模式（CS2 等竞技游戏）
#
# 特点：
#   - 幂等：已存在同名计划时直接复用并刷新参数，不会重复创建
#   - 不需要管理员权限（电源计划是按用户存储的）
#   - 不切换当前正在使用的计划，只创建/更新
#
# 运行：powershell -ExecutionPolicy Bypass -File setup_plans.ps1

$ErrorActionPreference = 'Continue'

$BALANCED_GUID = '381b4222-f694-41f0-9685-ff5bb260df2e' # 所有 Windows 都自带的“平衡”计划，用作复制母本

$SUB_PROC  = '54533251-82be-4824-96c1-47b60b740d00'
$SUB_USB   = '2a737441-1930-4402-8d77-b2bebba308a3'
$SUB_PCIE  = '501a4d13-42af-4429-9fd1-a8218c268e20'
$SUB_SLEEP = '238c9fa8-0aad-41ed-83f4-97be242c8f20'

$MIN        = '893dee8e-2bef-41e0-89c6-b55d0929964c'
$MAX        = 'bc5038f7-23e0-4960-96da-33abaf5935ec'
$BOOST      = 'be337238-0d82-4146-a960-4f3749d470c7'
$PARKMIN    = '0cc5b647-c1df-4637-891a-dec35c318583'
$COOLING    = '94d3a615-a899-4ac5-ae2b-e4d8f634367f'
$USBSUSPEND = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
$PCIELINK   = 'ee12f906-d277-404b-b6da-e5fa1a576df5'
$SLEEPIDLE  = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'

# 三档参数（笔记本 i7-12700H 实测调优；台式机同样适用）
#   Max=99% 表示关闭睿频：视频/开发场景安静低温，游戏保留 100% 睿频
$Modes = @(
    @{ Name = '电影模式'; Desc = '观影模式：关睿频、安静散热、观影中不休眠';
       Min = 5;  Max = 99;  Boost = 0; Park = 100; Cool = 1; Usb = 1; Pcie = 1; Sleep = 0 },
    @{ Name = 'AI模式';   Desc = 'AI 开发模式：关睿频降发热、USB/PCIe省电关、永不休眠，适合训练与推理长任务';
       Min = 20; Max = 99;  Boost = 0; Park = 100; Cool = 0; Usb = 0; Pcie = 0; Sleep = 0 },
    @{ Name = '游戏模式'; Desc = '游戏模式：睿频全开、核心不停驻、PCIe/USB省电关、永不休眠；最小状态由系统动态调频防过热';
       Min = 5;  Max = 100; Boost = 2; Park = 100; Cool = 0; Usb = 0; Pcie = 0; Sleep = 0 }
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

function Set-Both([string]$id, [string]$sub, [string]$setting, [string]$label, [int]$value) {
    powercfg /setacvalueindex $id $sub $setting $value 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host ("    跳过（此系统不支持）: " + $label); return }
    powercfg /setdcvalueindex $id $sub $setting $value 2>$null
}

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

    powercfg /changename $id $m.Name $m.Desc
    Set-Both $id $SUB_PROC  $MIN        '最小处理器状态' $m.Min
    Set-Both $id $SUB_PROC  $MAX        '最大处理器状态' $m.Max
    Set-Both $id $SUB_PROC  $BOOST      '睿频模式'       $m.Boost
    Set-Both $id $SUB_PROC  $PARKMIN    '核心停驻最小核心数' $m.Park
    Set-Both $id $SUB_PROC  $COOLING    '散热策略'       $m.Cool
    Set-Both $id $SUB_USB   $USBSUSPEND 'USB选择性暂停'  $m.Usb
    Set-Both $id $SUB_PCIE  $PCIELINK   'PCIe链路省电'   $m.Pcie
    Set-Both $id $SUB_SLEEP $SLEEPIDLE  '睡眠超时'       $m.Sleep
}

Write-Host ''
Write-Host '全部完成。当前电源计划列表：'
powercfg /list
Write-Host ''
Write-Host '提示：当前激活的计划未被修改。请通过 PowerPlanTray 托盘工具或控制面板切换到想要的模式。'
