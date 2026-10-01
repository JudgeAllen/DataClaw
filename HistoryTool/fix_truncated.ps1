# fix_truncated.ps1 — 补拉"溢出秒截断"窗口的缺失部分（迭代版，只读原始数据）
#
# 原理: 原文件在"秒内 tick > 1000 条"的时刻被 API 分页限制卡住；该秒之后的数据仍可取。
# 迭代: 每段（10 分钟）从 [起点, 段末] 拉取；若结果末行时间未达段末（说明又卡在某个溢出秒），
#       则从「末行时间 + 1 秒」继续拉，生成新碎片，直到覆盖段末或无法前进。
#       碎片命名: <原名>.part2.<段号:00>.<碎片:00>.csv（按名称排序即为时间序）
# 输出: <OutRoot>\ （原文件不动）
#
# 用法:
#   .\fix_truncated.ps1 -Mode BA -Shard 0 -Shards 4 -ClientId 90
#   .\fix_truncated.ps1 -Mode TR -Shard 0 -Shards 1 -ClientId 89

param(
    [ValidateSet('BA','TR')][string]$Mode = 'BA',
    [int]$Shard = 0,
    [int]$Shards = 1,
    [int]$ClientId = 90,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe',
    [int]$SegmentMinutes = 10,
    [int]$ConId = 770561204,
    [int]$MaxIterPerSegment = 60
)

$report = 'F:\Git\DataClaw\data\truncation_report.csv'
if ($Mode -eq 'BA') {
    $outRoot = 'F:\Git\DataClaw\data\nqu6_ticksba_fix'
    $what = 'BID_ASK'
    $trunc = Import-Csv $report | Where-Object { $_.Verdict -eq 'TRUNCATED' }
} else {
    $outRoot = 'F:\Git\DataClaw\data\nqu6_ticks_fix'
    $what = 'TRADES'
    $trunc = @(
        [pscustomobject]@{ File='NQU6_TICKS_20260901_12.csv'; LastBA='2026-09-01 12:04:43' },
        [pscustomobject]@{ File='NQU6_TICKS_20260901_20.csv'; LastBA='2026-09-01 20:23:46' },
        [pscustomobject]@{ File='NQU6_TICKS_20260902_09.csv'; LastBA='2026-09-02 09:37:47' }
    )
}
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
$logFile = Join-Path $outRoot ("_fix.shard{0}.log" -f $Shard)

# ---- 构建段任务列表 ----
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
            Base = ($x.File -replace '\.csv$','')
            Seg  = $n
            Start = $segStart.ToString('yyyyMMdd HH:mm:ss')
            End   = $segEnd.ToString('yyyyMMdd HH:mm:ss')
        })
        $segStart = $segEnd.AddSeconds(1)
    }
}

# ---- 分片 ----
$mine = @()
for ($i = 0; $i -lt $tasks.Count; $i++) { if (($i % $Shards) -eq $Shard) { $mine += $tasks[$i] } }
Write-Host "[$Mode shard $($Shard+1)/$Shards] 总段数 $($tasks.Count)，本片 $($mine.Count) 段"

$doneSeg = 0; $skipSeg = 0; $failed = @(); $noProgress = @()
$k = 0
foreach ($t in $mine) {
    $k++
    $prefix = "{0}.part2.{1:D2}" -f $t.Base, $t.Seg
    $segEnd = [datetime]::ParseExact($t.End, 'yyyyMMdd HH:mm:ss', $null)
    $cur    = [datetime]::ParseExact($t.Start, 'yyyyMMdd HH:mm:ss', $null)
    $nextFrag = 1

    # 已有碎片：判断是否已覆盖段末，否则从最后碎片末尾续拉
    $existing = @(Get-ChildItem $outRoot -Filter "$prefix.*.csv" -ErrorAction SilentlyContinue | Sort-Object Name)
    $covered = $false
    if ($existing.Count -gt 0) {
        $last = $existing[-1]
        if ($last.Name -match '\.(\d+)\.csv$') { $nextFrag = [int]$matches[1] + 1 }
        $ll = Get-Content $last.FullName -Tail 1 -ErrorAction SilentlyContinue
        if ($ll -and $ll.Length -ge 19) {
            $ld = [datetime]::ParseExact($ll.Substring(0,19), 'yyyy-MM-dd HH:mm:ss', $null)
            if ($ld -ge $segEnd) { $covered = $true }
            elseif ($ld -ge $cur) { $cur = $ld.AddSeconds(1) }
        }
    }
    if ($covered) { $skipSeg++; continue }

    $iter = 0; $segOk = $false
    while ($cur -lt $segEnd -and $iter -lt $MaxIterPerSegment) {
        $iter++
        $out = Join-Path $outRoot ("{0}.{1:D2}.csv" -f $prefix, $nextFrag)
        $startArg = $cur.ToString('yyyyMMdd HH:mm:ss')
        $endArg   = $segEnd.ToString('yyyyMMdd HH:mm:ss')

        $got = $false
        for ($try = 1; $try -le 3; $try++) {
            Write-Host "  [$k/$($mine.Count)] $prefix frag$nextFrag iter$iter $startArg .. $endArg (尝试$try)"
            $global:LASTEXITCODE = 0
            & $Exe --ticks --symbol NQ --sectype FUT --exchange CME --currency USD --conid $ConId `
                --client-id $ClientId --what-to-show $what --start $startArg --end $endArg `
                --request-timeout 120 --max-pages 1500 --output $out *>> $logFile
            $code = $LASTEXITCODE
            if ($code -eq 0) { $got = $true; break }
            # 失败但已写出部分数据：保留，交给下面的"从末行续拉"逻辑，避免整段重拉
            if (Test-Path $out) {
                Write-Host "    ⚠ exit=$code 但已有部分数据，保留并从末尾续拉"
                $got = $true
                break
            }
            if ($try -lt 3) { Start-Sleep -Seconds 60 }
        }
        if (-not $got) { $failed += "$prefix frag$nextFrag"; break }

        if (-not (Test-Path $out)) {
            # 该窗口无 tick（服务器无数据）——本段结束
            $segOk = $true; break
        }
        $ll = Get-Content $out -Tail 1
        if (-not $ll -or $ll.Length -lt 19) { $segOk = $true; break }
        $ld = [datetime]::ParseExact($ll.Substring(0,19), 'yyyy-MM-dd HH:mm:ss', $null)

        if ($ld -ge $segEnd) { $segOk = $true; break }        # 覆盖段末
        if ($ld -lt $cur) { $noProgress += "$prefix frag$nextFrag (卡在 $($ld.ToString('HH:mm:ss')))"; break }
        $cur = $ld.AddSeconds(1)
        $nextFrag++
    }
    if (-not $segOk -and $cur -ge $segEnd) { $segOk = $true }   # 仅剩 <1 秒（start==end 非法）视为完成
    if ($segOk) { $doneSeg++ }
}

Write-Host ""
Write-Host "[$Mode shard $($Shard+1)/$Shards] 段完成 $doneSeg | 跳过(已覆盖) $skipSeg | 失败 $($failed.Count) | 无进展(API 卡点) $($noProgress.Count)"
if ($failed.Count -gt 0) { $failed | ForEach-Object { Write-Host "  失败: $_" } }
if ($noProgress.Count -gt 0) { $noProgress | ForEach-Object { Write-Host "  卡点: $_" } }
if ($failed.Count -gt 0) { exit 1 }
