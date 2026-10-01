# verify_ticks_coverage.ps1 — 校验 nqu6 分小时 tick 目录的覆盖完整性（只读，不修改任何文件）
#
# 用法:
#   .\verify_ticks_coverage.ps1                      # 校验 nqu6_ticksba_hours（BID_ASK）
#   .\verify_ticks_coverage.ps1 -Dir ..\data\nqu6_ticks_hours   # 校验 TRADES
#   .\verify_ticks_coverage.ps1 -Start 20260612 -End 20260903   # 显式区间（End 含当天 00 时起全部有效小时）
#
# 有效小时规则（BJT，DST 生效期）:
#   - 周日全天无数据
#   - 周一 00-04 无数据（美盘周日 18:00 ET 才开盘）；周一 05 起有效
#   - 每日 05 时 = 美东 17:00-18:00 结算/维护窗口（CME），无数据
#   - 周六 06 时起无数据（美东周五 18:00 ET 收盘）；周六 00-05 有效
# 输出: 缺失小时清单 + 统计；若只给 -Start 不给 -End，终点取目录内最新文件所在小时，
#       只查内部缺口（适合拉取刚结束后的完整性核查）。

param(
    [string]$Dir = 'F:\Git\DataClaw\data\nqu6_ticksba_hours',
    [string]$Start = '20260612',
    [string]$End = '',          # 空 = 用目录内最新 csv 的小时
    [string]$Prefix = 'NQU6_TICKSBA'   # 文件名前缀，TRADES 目录传 'NQU6_TICKS'
)

$files = Get-ChildItem $Dir -Filter '*.csv' -ErrorAction SilentlyContinue
$partials = Get-ChildItem $Dir -Filter '*.partial' -ErrorAction SilentlyContinue

if (-not $files) { Write-Host "目录 $Dir 没有 csv 文件" -ForegroundColor Red; exit 1 }

# 解析已有文件 -> set of "yyyyMMdd HH"
$have = @{}
foreach ($f in $files) {
    if ($f.Name -match '(\d{8})_(\d{2})\.csv$') { $have["$($matches[1]) $($matches[2])"] = $true }
}
$attempted = @{}
foreach ($f in $partials) {
    if ($f.Name -match '(\d{8})_(\d{2})\.partial$') { $attempted["$($matches[1]) $($matches[2])"] = $true }
}

# 终点小时：显式 End 取 End 当天 00 前，即 [Start 00:00, End 00:00) 闭开区间检查到 End-1 天
$endHour = ''
if ($End) {
    $endDate = [datetime]::ParseExact($End, 'yyyyMMdd', $null)
    $endHour = $endDate.AddDays(-1).ToString('yyyyMMdd') + ' 23'
} else {
    $newest = ($files | ForEach-Object {
        if ($_.Name -match '(\d{8})_(\d{2})\.csv$') { "$($matches[1]) $($matches[2])" }
    } | Sort-Object | Select-Object -Last 1)
    $endHour = $newest
}

$t = [datetime]::ParseExact($Start + ' 00', 'yyyyMMdd HH', $null)
$tEnd = [datetime]::ParseExact($endHour, 'yyyyMMdd HH', $null)
Write-Host "检查区间: $($t.ToString('yyyy-MM-dd HH:mm')) 至 $($tEnd.ToString('yyyy-MM-dd HH:mm'))"

$missing = @()
$attemptedOnly = @()
while ($t -le $tEnd) {
    $dow = $t.DayOfWeek  # Sunday..Saturday
    $hh = $t.Hour
    $skip = $false
    if ($dow -eq [DayOfWeek]::Sunday) { $skip = $true }
    elseif ($dow -eq [DayOfWeek]::Monday -and $hh -le 4) { $skip = $true }
    elseif ($hh -eq 5) { $skip = $true }
    elseif ($dow -eq [DayOfWeek]::Saturday -and $hh -ge 6) { $skip = $true }
    $key = $t.ToString('yyyyMMdd HH')
    if (-not $skip) {
        if (-not $have[$key]) {
            if ($attempted[$key]) { $attemptedOnly += $key }
            else { $missing += $key }
        }
    }
    $t = $t.AddHours(1)
}

$totalValid = 0
$t2 = [datetime]::ParseExact($Start + ' 00', 'yyyyMMdd HH', $null)
while ($t2 -le $tEnd) {
    $dow = $t2.DayOfWeek; $hh = $t2.Hour
    $skip = ($dow -eq [DayOfWeek]::Sunday) -or ($dow -eq [DayOfWeek]::Monday -and $hh -le 4) -or ($hh -eq 5) -or ($dow -eq [DayOfWeek]::Saturday -and $hh -ge 6)
    if (-not $skip) { $totalValid++ }
    $t2 = $t2.AddHours(1)
}

Write-Host ("CSV 文件: {0} | 有效小时总数: {1} | 缺失(未尝试): {2} | 失败保留(.partial): {3}" -f $files.Count, $totalValid, $missing.Count, ($attemptedOnly.Count + $partials.Count))
if ($missing.Count -gt 0) {
    Write-Host "`n== 缺失小时（完全没尝试过）==" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
if ($attemptedOnly.Count -gt 0) {
    Write-Host "`n== 失败残留（.partial 存在，无 csv）==" -ForegroundColor Red
    $attemptedOnly | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
if ($missing.Count -eq 0 -and $attemptedOnly.Count -eq 0) {
    Write-Host "`n✓ 全覆盖，无缺口" -ForegroundColor Green
}
