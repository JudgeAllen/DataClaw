# fetch_ba_loop.ps1 — BID_ASK 主循环（断点续传版）
#
# 用途: 设备重启 / 循环被杀后，一键恢复 BA 拉取。
# 行为:
#   - 起点 = 上一个完整小时（自动覆盖重启期间新产生的小时），终点 = 2026-06-12 00:00
#   - 倒序逐小时窗口；无效时段跳过（周日全天 / 周一00-04 / 每日05结算 / 周六06+）
#   - 已有 .csv 跳过（断点）；遇到 .partial 先重试该窗口
#   - 单窗口失败重试 3 次（间隔 60s）；连续 3 个窗口失败 = 1 次全局暂停（等 120s）；
#     累计 10 次全局暂停 → 停止（数据不丢，重启本脚本即可续）
#   - 窗口取 [HH:00:00, HH:59:59]
# 合约: NQU6 futures (conId 770561204), 端口 7497, clientId 97

param(
    [string]$Dir = 'F:\Git\DataClaw\data\nqu6_ticksba_hours',
    [string]$Prefix = 'NQU6_TICKSBA',
    [string]$WhatToShow = 'BID_ASK',
    [string]$EndDate = '20260612',
    [int]$ConId = 770561204,
    [int]$ClientId = 97,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

if (-not (Test-Path $Exe)) { Write-Host "找不到 exe: $Exe"; exit 1 }
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }

$t = (Get-Date).AddHours(-1)      # 上一个完整小时
$startHour = [datetime]::new($t.Year, $t.Month, $t.Day, $t.Hour, 0, 0)
$tEnd = [datetime]::ParseExact($EndDate + ' 00', 'yyyyMMdd HH', $null)

$pauseCount = 0
$consecFail = 0
$done = 0
$skipped = 0
$retried = 0

Write-Host "BA 循环启动: $($startHour.ToString('yyyy-MM-dd HH:mm')) → $($tEnd.ToString('yyyy-MM-dd HH:mm')) (倒序)"

$dt = $startHour
while ($dt -ge $tEnd) {
    $dow = $dt.DayOfWeek
    $hh = $dt.Hour
    $valid = -not (($dow -eq [DayOfWeek]::Sunday) -or ($dow -eq [DayOfWeek]::Monday -and $hh -le 4) -or ($hh -eq 5) -or ($dow -eq [DayOfWeek]::Saturday -and $hh -ge 6))
    if (-not $valid) { $dt = $dt.AddHours(-1); continue }

    $date = $dt.ToString('yyyyMMdd')
    $hhs = $dt.ToString('HH')
    $base = "${Prefix}_${date}_${hhs}"
    $csv = Join-Path $Dir "$base.csv"
    $partial = Join-Path $Dir "$base.csv.partial"
    $log = Join-Path $Dir "$base.log"

    if (Test-Path $csv) { $skipped++; $dt = $dt.AddHours(-1); continue }

    # partial 存在 = 上次失败残留，先清掉重拉
    if (Test-Path $partial) {
        Remove-Item $partial -Force
        Write-Host "$date $hhs retry(partial)"
        $retried++
    }

    $ok = $false
    for ($try = 1; $try -le 3; $try++) {
        $start = "$date $hhs`:00:00"
        $end = "$date $hhs`:59:59"
        Write-Host "$date $hhs fetching... (尝试 $try/3)"
        & $Exe --ticks --symbol NQ --sectype FUT --exchange CME --currency USD `
            --local-symbol NQU6 --last-trade-date 202609 --trading-class NQ `
            --conid $ConId --client-id $ClientId --what-to-show $WhatToShow `
            --start $start --end $end --output $partial --request-timeout 120 *>> $log
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            if (Test-Path $partial) { Move-Item $partial $csv -Force }
            Write-Host "$date $hhs ok"
            $done++
            $consecFail = 0
            $ok = $true
            break
        }
        Write-Host "$date $hhs FAILED exit=$code"
        if ($try -lt 3) { Start-Sleep -Seconds 60 }
    }

    if (-not $ok) {
        $consecFail++
        if ($consecFail -ge 3) {
            $pauseCount++
            Write-Host "连续 3 窗口失败 → 全局暂停 $pauseCount/10，等 120s 后继续"
            if ($pauseCount -ge 10) {
                Write-Host "已达 10 次全局暂停，循环停止（断点保留，重启本脚本续跑）"
                exit 1
            }
            Start-Sleep -Seconds 120
            $consecFail = 0
        }
    }
    $dt = $dt.AddHours(-1)
}

Write-Host ""
Write-Host "==== 完成: 已到 $EndDate 00:00 ===="
Write-Host "完成: $done | 跳过(已有): $skipped | retry(partial): $retried | 全局暂停: $pauseCount"
