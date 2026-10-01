# check_truncation.ps1 — 交叉验证 BA 尾部缺口是真截断还是真无数据（只读）
#
# 逻辑: 对每个 BA "尾部缺口"文件（末行时间 T_last 距小时结束 > 20 分钟），
#       读同小时 TRADES 文件，统计 T_last 之后仍有多少笔成交：
#         有成交 -> 真截断（BA 缺失该时段数据）
#         无成交 -> 该时段无成交（可能真无盘口活动，标记待定）
# 输出报告: data\truncation_report.csv

param(
    [string]$BaDir  = 'F:\Git\DataClaw\data\nqu6_ticksba_hours',
    [string]$TrDir  = 'F:\Git\DataClaw\data\nqu6_ticks_hours',
    [string]$Report = 'F:\Git\DataClaw\data\truncation_report.csv',
    [int]$GapMinutes = 20
)

$rows = Import-Csv 'F:\Git\DataClaw\data\integrity_report.csv'
$baRows = $rows | Where-Object { $_.Dir -eq 'BA' -and [long]$_.Lines -gt 0 }

$out = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($x in $baRows) {
    if ($x.File -notmatch '_(\d{8})_(\d{2})\.csv$') { continue }
    $day = $matches[1]; $hh = $matches[2]
    $expEnd = [datetime]::ParseExact("$day $hh`:59:59", 'yyyyMMdd HH:mm:ss', $null)
    $last = [datetime]::ParseExact($x.Last, 'yyyy-MM-dd HH:mm:ss', $null)
    $gapMin = ($expEnd - $last).TotalMinutes
    if ($gapMin -le $GapMinutes) { continue }

    $i++
    $trFile = Join-Path $TrDir "NQU6_TICKS_${day}_${hh}.csv"
    $trAfter = 0
    $trLast = ''
    if (Test-Path $trFile) {
        # TRADES 文件按时间升序，统计 T_last 之后的成交
        $lines = [System.IO.File]::ReadLines($trFile)
        $first = $true
        foreach ($ln in $lines) {
            if ($first) { $first = $false; continue }
            if ($ln.Length -lt 19) { continue }
            $t = $ln.Substring(0, 19)
            if ([string]::CompareOrdinal($t, $x.Last) -gt 0) { $trAfter++; $trLast = $t }
        }
    }

    $out.Add([pscustomobject]@{
        File      = $x.File
        LastBA    = $x.Last
        BaLines   = $x.Lines
        GapMin    = [math]::Round($gapMin, 1)
        TrAfter   = $trAfter
        TrLast    = $trLast
        Verdict   = if ($trAfter -gt 0) { 'TRUNCATED' } else { 'NO_TRADES_AFTER' }
    })
}

$out | Sort-Object File | Export-Csv -Path $Report -NoTypeInformation -Encoding UTF8
$trunc = $out | Where-Object { $_.Verdict -eq 'TRUNCATED' }
$none  = $out | Where-Object { $_.Verdict -eq 'NO_TRADES_AFTER' }
Write-Host "BA 尾部缺口文件: $($out.Count) 个"
Write-Host "  真截断(缺口时段有成交): $($trunc.Count)"
Write-Host "  缺口时段无成交(待定): $($none.Count)"
Write-Host "报告: $Report"
Write-Host ""
Write-Host "== 真截断样例(前 15) =="
$trunc | Sort-Object File | Select-Object -First 15 | ForEach-Object {
    Write-Host ("  {0}  last={1}  缺口={2}min  缺口内成交={3} 笔" -f $_.File, $_.LastBA, $_.GapMin, $_.TrAfter)
}
Write-Host ""
Write-Host "== 缺口时段成交最多的 10 个 =="
$trunc | Sort-Object -Property { [int]$_.TrAfter } -Descending | Select-Object -First 10 | ForEach-Object {
    Write-Host ("  {0}  last={1}  缺口={2}min  缺口内成交={3} 笔" -f $_.File, $_.LastBA, $_.GapMin, $_.TrAfter)
}
