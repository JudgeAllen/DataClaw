# fetch_5s_all.ps1 — 拉取 NQ/ES × Z5/H6/M6/U6 共 8 个合约的 5 秒 K 线（每段 1 天，断点续传，自动合并）
#
# 时间范围与 1m 数据一致；IB 对 5 秒 bar 有历史深度限制（实测约 11-12 个月），
# 超出的段会失败并记录（脚本不中断）。
#
# 输出：段文件 <OutRoot>\<TAG>\<TAG>_5s_YYYYMMDD.csv
#       合并   <OutRoot>\<TAG>_5s_<起>_<止>.csv

param(
    [string]$OutRoot = 'F:\Git\DataClaw\data\bars_5s',
    [int]$ClientId = 88,
    [int]$MaxRetries = 1,
    [int]$SegmentDays = 2,
    [int]$Shard = 0,
    [int]$Shards = 1,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

$jobs = @(
    @{ Tag='NQZ5'; Sym='NQ'; ConId=563947738; Start='20250815 06:00:00'; End='20251219 23:59:00'; Expired=$true  },
    @{ Tag='NQH6'; Sym='NQ'; ConId=730283097; Start='20251115 07:00:00'; End='20260320 23:59:00'; Expired=$true  },
    @{ Tag='NQM6'; Sym='NQ'; ConId=750150196; Start='20260216 06:00:00'; End='20260618 23:59:00'; Expired=$true  },
    @{ Tag='NQU6'; Sym='NQ'; ConId=770561204; Start='20260503 00:00:00'; End='20260915 04:59:00'; Expired=$false },
    @{ Tag='ESZ5'; Sym='ES'; ConId=495512563; Start='20250815 06:00:00'; End='20251219 23:59:00'; Expired=$true  },
    @{ Tag='ESH6'; Sym='ES'; ConId=649180695; Start='20251115 07:00:00'; End='20260320 23:59:00'; Expired=$true  },
    @{ Tag='ESM6'; Sym='ES'; ConId=649180678; Start='20260216 06:00:00'; End='20260618 23:59:00'; Expired=$true  },
    @{ Tag='ESU6'; Sym='ES'; ConId=649180671; Start='20260503 00:00:00'; End='20260915 04:59:00'; Expired=$false }
)

# 分片（按合约轮转分配）
if ($Shards -gt 1) {
    $mine = @()
    for ($k = 0; $k -lt $jobs.Count; $k++) { if (($k % $Shards) -eq $Shard) { $mine += $jobs[$k] } }
    $jobs = $mine
    Write-Host ("[5s shard {0}/{1}] 负责合约: {2}" -f ($Shard + 1), $Shards, (($jobs | ForEach-Object { $_.Tag }) -join ', '))
}

if (-not (Test-Path $Exe)) { Write-Host "找不到 exe: $Exe"; exit 1 }
New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null

foreach ($j in $jobs) {
    $tag = $j.Tag
    $segDir = Join-Path $OutRoot $tag
    New-Item -ItemType Directory -Path $segDir -Force | Out-Null

    $t = [datetime]::ParseExact($j.Start, 'yyyyMMdd HH:mm:ss', $null)
    $tEnd = [datetime]::ParseExact($j.End, 'yyyyMMdd HH:mm:ss', $null)

    Write-Host "==== $tag : $($j.Start) -> $($j.End) ===="
    $failed = @(); $done = 0; $noData = 0; $i = 0
    while ($t -lt $tEnd) {
        $i++
        $dayStart = $t
        $dayEnd = $t.Date.AddDays($SegmentDays).AddSeconds(-1)
        if ($dayEnd -gt $tEnd) { $dayEnd = $tEnd }
        $ds = $dayStart.ToString('yyyyMMdd')
        $csv = Join-Path $segDir "${tag}_5s_${ds}.csv"
        $log = Join-Path $segDir "${tag}_5s_${ds}.log"

        if (Test-Path $csv) { $t = $t.Date.AddDays($SegmentDays); continue }

        $startArg = $dayStart.ToString('yyyyMMdd HH:mm:ss')
        $endArg   = $dayEnd.ToString('yyyyMMdd HH:mm:ss')
        $ok = $false
        for ($try = 1; $try -le $MaxRetries; $try++) {
            & $Exe --symbol $j.Sym --sectype FUT --exchange CME --currency USD --conid $j.ConId `
                --client-id $ClientId --bar-size '5 secs' --what-to-show TRADES `
                --start $startArg --end $endArg --duration "$($SegmentDays) D" --request-timeout 90 `
                --output $csv *>> $log
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                if (Test-Path $csv) { $done++ } else { $noData++ }   # 无文件 = 该段无数据（周末/假日），正常
                $ok = $true
                break
            }
            if (Test-Path $csv) { Remove-Item $csv -Force -ErrorAction SilentlyContinue }
            if ($try -lt $MaxRetries) { Start-Sleep -Seconds 20 }
        }
        if (-not $ok) { $failed += $ds; Write-Host "  [$i] $ds 失败" }
        $t = $t.Date.AddDays($SegmentDays)
    }

    # 合并（段文件按名称排序 = 时间序；跳过 header；相邻去重）
    $segs = Get-ChildItem $segDir -Filter "${tag}_5s_*.csv" | Sort-Object Name
    if ($segs.Count -gt 0) {
        $seen = New-Object 'System.Collections.Generic.HashSet[string]'
        $rows = New-Object 'System.Collections.Generic.List[string]'
        $hdr = $null
        foreach ($sf in $segs) {
            $first = $true
            foreach ($line in [System.IO.File]::ReadLines($sf.FullName)) {
                if ($first) { if ($hdr -eq $null) { $hdr = $line }; $first = $false; continue }
                if ($line.Length -lt 19) { continue }
                if ($seen.Add($line)) { $rows.Add($line) }
            }
        }
        $rows.Sort()
        if ($rows.Count -gt 0) {
            $merged = Join-Path $OutRoot ("{0}_5s_{1}_{2}.csv" -f $tag, $rows[0].Substring(0,10).Replace('-',''), $rows[$rows.Count-1].Substring(0,10).Replace('-',''))
            $all = New-Object 'System.Collections.Generic.List[string]'
            $all.Add($hdr); $all.AddRange($rows)
            [System.IO.File]::WriteAllLines($merged, $all)
            Write-Host ("  ==> {0}: {1} 段 -> {2} 行 -> {3}" -f $tag, $segs.Count, $rows.Count, (Split-Path $merged -Leaf))
        }
    }
    Write-Host ("  [$tag] 完成 {0} 段，失败 {1} 段{2}" -f $done, $failed.Count, $(if ($failed.Count -gt 0) { "（受 IB 历史深度限制）: " + ($failed -join ', ') } else { "" }))
}
Write-Host "全部合约处理完毕"
