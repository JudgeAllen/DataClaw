# fetch_hour_gaps.ps1 — 自动补拉指定日期区间内缺失的小时窗口（tick 模式）
#
# 用法:
#   .\fetch_hour_gaps.ps1 -Dir ..\data\nqu6_ticks_hours -Prefix NQU6_TICKS -WhatToShow TRADES `
#       -Start 20260901 -End 20260903 -ClientId 98
#
# 行为:
#   - 枚举 [Start, End] 内所有“有效小时”（周日/周一00-04/每日05结算/周六06+ 跳过）
#   - 已有 <prefix>_<yyyyMMdd>_<HH>.csv 的窗口自动跳过（断点续传）
#   - 写 <prefix>_<yyyyMMdd>_<HH>.csv.partial，exit 0 后改名为 .csv
#   - 失败自动重试 3 次（间隔 60s，防 TWS 农场瞬时断线），仍失败则保留 .partial 并汇报
#   - 窗口取 [HH:00:00, HH:59:59]，与相邻既有文件无缝衔接
# 合约: NQU6 futures (conId 770561204, expiry 20260918), 端口 7497 纸面

param(
    [string]$Dir = 'F:\Git\DataClaw\data\nqu6_ticks_hours',
    [string]$Prefix = 'NQU6_TICKS',
    [string]$WhatToShow = 'TRADES',
    [string]$Start = '20260901',
    [string]$End = '20260903',
    [int]$ConId = 770561204,
    [int]$ClientId = 98,
    [int]$MaxRetries = 3,
    [int]$RetryDelaySec = 60,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

if (-not (Test-Path $Exe)) { Write-Host "找不到 exe: $Exe"; exit 1 }
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Path $Dir -Force | Out-Null }

# ---- 枚举缺失窗口 ----
$missing = @()
$t = [datetime]::ParseExact($Start + ' 00', 'yyyyMMdd HH', $null)
$tEnd = [datetime]::ParseExact($End + ' 23', 'yyyyMMdd HH', $null)
while ($t -le $tEnd) {
    $dow = $t.DayOfWeek
    $hh = $t.Hour
    $skip = ($dow -eq [DayOfWeek]::Sunday) -or ($dow -eq [DayOfWeek]::Monday -and $hh -le 4) -or ($hh -eq 5) -or ($dow -eq [DayOfWeek]::Saturday -and $hh -ge 6)
    if ($t.AddHours(1) -gt (Get-Date)) { $skip = $true }   # 未结束的当前小时不拉（数据不全）
    if (-not $skip) {
        $base = "${Prefix}_$($t.ToString('yyyyMMdd'))_$($t.ToString('HH'))"
        if (-not (Test-Path (Join-Path $Dir "$base.csv"))) { $missing += $t }
    }
    $t = $t.AddHours(1)
}

Write-Host "待补窗口: $($missing.Count) 个"
if ($missing.Count -eq 0) { Write-Host '区间内无缺口 ✓'; exit 0 }
$missing | ForEach-Object { Write-Host "  $($_.ToString('yyyyMMdd HH'))" }

# ---- 逐个补拉 ----
$failed = @()
$done = 0
$i = 0
foreach ($dt in $missing) {
    $i++
    $date = $dt.ToString('yyyyMMdd')
    $hh = $dt.ToString('HH')
    $base = "${Prefix}_${date}_${hh}"
    $csv = Join-Path $Dir "$base.csv"
    $partial = Join-Path $Dir "$base.csv.partial"
    $log = Join-Path $Dir "$base.log"

    # 多进程并行时可能已被其他实例补上，再查一次
    if (Test-Path $csv) { Write-Host "跳过(已有): $base.csv"; continue }

    Write-Host "[$i/$($missing.Count)] $date $hh 开始"
    $ok = $false
    for ($try = 1; $try -le $MaxRetries; $try++) {
        $start = "$date $hh`:00:00"
        $end = "$date $hh`:59:59"
        Write-Host "  尝试 $try/$MaxRetries : $start .. $end"
        & $Exe --ticks --symbol NQ --sectype FUT --exchange CME --currency USD `
            --local-symbol NQU6 --last-trade-date 202609 --trading-class NQ `
            --conid $ConId --client-id $ClientId --what-to-show $WhatToShow `
            --start $start --end $end --output $partial --request-timeout 120 *>> $log
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            if (Test-Path $partial) { Move-Item $partial $csv -Force }
            Write-Host "  ✓ 完成"
            $ok = $true
            $done++
            break
        } else {
            Write-Host "  ✗ exit=$code (日志: $log)"
            if ($try -lt $MaxRetries) { Start-Sleep -Seconds $RetryDelaySec }
        }
    }
    if (-not $ok) { $failed += "$date $hh" }
}

Write-Host ""
Write-Host "==== 汇总 ===="
Write-Host "完成: $done | 失败: $($failed.Count)"
if ($failed.Count -gt 0) {
    Write-Host "失败窗口(保留 .partial):" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "全部窗口完成 ✓"
