# check_fix_coverage.ps1 — 校验 934 个补丁段的覆盖情况（只读）
#
# 对每个段：找到它的所有碎片文件（按名排序 = 时间序），读最后一个碎片的末行时间：
#   末行 >= 段末  -> 覆盖完成
#   否则          -> 未完成（记录缺口范围）
# 输出：完成/未完成统计 + 未完成段清单（含缺口起点）

param(
    [string]$FixDir  = 'F:\Git\DataClaw\data\nqu6_ticksba_fix',
    [string]$Report  = 'F:\Git\DataClaw\data\fix_coverage_report.csv',
    [int]$SegmentMinutes = 10
)

$trunc = Import-Csv 'F:\Git\DataClaw\data\truncation_report.csv' | Where-Object { $_.Verdict -eq 'TRUNCATED' }

# 构建段列表（与 fix_truncated.ps1 相同逻辑）
$tasks = New-Object System.Collections.Generic.List[object]
foreach ($x in $trunc) {
    if ($x.File -notmatch '_(\d{8})_(\d{2})\.csv$') { continue }
    $day = $matches[1]; $hh = $matches[2]
    $hourEnd = [datetime]::ParseExact("$day $hh`:59:59", 'yyyyMMdd HH:mm:ss', $null)
    $segStart = ([datetime]::ParseExact($x.LastBA, 'yyyy-MM-dd HH:mm:ss', $null)).AddSeconds(1)
    if ($segStart -gt $hourEnd) { continue }
    $n = 0
    while ($segStart -lt $hourEnd) {
        $segEnd = $segStart.AddMinutes($SegmentMinutes).AddSeconds(-1)
        if ($segEnd -gt $hourEnd) { $segEnd = $hourEnd }
        $n++
        $tasks.Add([pscustomobject]@{
            Base = ($x.File -replace '\.csv$',''); Seg = $n
            Start = $segStart.ToString('yyyyMMdd HH:mm:ss'); End = $segEnd.ToString('yyyyMMdd HH:mm:ss')
        })
        $segStart = $segEnd.AddSeconds(1)
    }
}

Write-Host "段总数: $($tasks.Count)"

# 预取碎片清单
$files = Get-ChildItem $FixDir -Filter '*.part2.*.csv'
$map = @{}
foreach ($f in $files) {
    if ($f.Name -match '^(.*\.part2\.\d{2})\.\d+\.csv$') {
        $k = $matches[1]
        if (-not $map.ContainsKey($k)) { $map[$k] = New-Object System.Collections.Generic.List[object] }
        $map[$k].Add($f)
    }
}
Write-Host "碎片文件: $($files.Count)，覆盖段 prefix: $($map.Count)"

$rows = New-Object System.Collections.Generic.List[object]
$done = 0; $missing = 0; $noFrag = 0
$i = 0
foreach ($t in $tasks) {
    $i++
    if ($i % 100 -eq 0) { Write-Host "  .. $i/$($tasks.Count)" }
    $prefix = "{0}.part2.{1:D2}" -f $t.Base, $t.Seg
    $segEnd = [datetime]::ParseExact($t.End, 'yyyyMMdd HH:mm:ss', $null)

    if (-not $map.ContainsKey($prefix)) {
        $noFrag++
        $rows.Add([pscustomobject]@{ Base=$t.Base; Seg=$t.Seg; Status='NO_FRAGMENT'; LastTime=''; GapTo=$t.End })
        continue
    }
    $frags = $map[$prefix] | Sort-Object Name
    $last = $frags[-1]
    $ll = Get-Content $last.FullName -Tail 1 -ErrorAction SilentlyContinue
    $lastTime = ''
    $ok = $false
    if ($ll -and $ll.Length -ge 19) {
        $lastTime = $ll.Substring(0,19)
        $ld = [datetime]::ParseExact($lastTime, 'yyyy-MM-dd HH:mm:ss', $null)
        if ($ld -ge $segEnd) { $ok = $true }
    }
    if ($ok) { $done++; $rows.Add([pscustomobject]@{ Base=$t.Base; Seg=$t.Seg; Status='COVERED'; LastTime=$lastTime; GapTo='' }) }
    else {
        $missing++
        $rows.Add([pscustomobject]@{ Base=$t.Base; Seg=$t.Seg; Status='INCOMPLETE'; LastTime=$lastTime; GapTo=$t.End })
    }
}

$rows | Sort-Object Base, Seg | Export-Csv -Path $Report -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "==== 覆盖校验结果 ===="
Write-Host "覆盖完成: $done / $($tasks.Count)"
Write-Host "未完成(有碎片但未达段末): $missing"
Write-Host "完全没有碎片: $noFrag"
Write-Host "报告: $Report"
if ($missing -gt 0) {
    Write-Host ""
    Write-Host "未完成段(前 20):"
    $rows | Where-Object { $_.Status -eq 'INCOMPLETE' } | Select-Object -First 20 | ForEach-Object {
        Write-Host ("  {0} seg{1}  已到 {2}  段末 {3}" -f $_.Base, $_.Seg, $_.LastTime, $_.GapTo)
    }
}
if ($noFrag -gt 0) {
    Write-Host ""
    Write-Host "无碎片段(前 10):"
    $rows | Where-Object { $_.Status -eq 'NO_FRAGMENT' } | Select-Object -First 10 | ForEach-Object {
        Write-Host ("  {0} seg{1}" -f $_.Base, $_.Seg)
    }
}
