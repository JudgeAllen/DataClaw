# fetch_1m_loop.ps1 — 拉取 NQ / ES 系列合约 1 分钟 K 线（主力时间段，断点续传）
#
# 合约与区间（BJT 墙上时间）:
#   NQU6 (202609, conid 770561204): 20260615 00:00 -> 20260906 12:00
#   NQM6 (202606, conid 750150196): 20260316 00:00 -> 20260618 23:59  (已过期)
#   NQH6 (202603, conid 730283097): 20251215 00:00 -> 20260320 23:59  (已过期)
#   NQZ5 (202512, conid 563947738): 20250915 00:00 -> 20251219 23:59  (已过期)
#   ESZ5 (202512, conid 495512563): 20250915 00:00 -> 20251219 23:59  (已过期)
#   ESH6 (202603, conid 649180695): 20251215 00:00 -> 20260320 23:59  (已过期)
#   ESM6 (202606, conid 649180678): 20260316 00:00 -> 20260618 23:59  (已过期)
#   ESU6 (202609, conid 649180671): 20260615 00:00 -> 20260906 12:00
#
# 行为:
#   - 每 ~10 天为一段，段文件存 <OutRoot>\<合约>\segments\
#   - 段 csv 存在即跳过；失败重试 3 次（间隔 60s），仍失败记录并在末尾报告
#   - 全部段完成后自动合并为 <OutRoot>\<合约>_1m_<开始>_<结束>.csv
#   - bar 请求: --bar-size "1 min" --what-to-show TRADES，duration 30 D（自动按段长收窄）

param(
    [string]$OutRoot = 'F:\Git\DataClaw\data\bars_1m',
    [int]$ClientId = 92,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

$jobs = @(
    @{ Tag='NQU6'; Sym='NQ'; ConId=770561204; Start='20260615 00:00:00'; End='20260906 12:00:00'; Expired=$false },
    @{ Tag='NQM6'; Sym='NQ'; ConId=750150196; Start='20260316 00:00:00'; End='20260618 23:59:00'; Expired=$true  },
    @{ Tag='NQH6'; Sym='NQ'; ConId=730283097; Start='20251215 00:00:00'; End='20260320 23:59:00'; Expired=$true  },
    @{ Tag='NQZ5'; Sym='NQ'; ConId=563947738; Start='20250915 00:00:00'; End='20251219 23:59:00'; Expired=$true  },
    @{ Tag='ESZ5'; Sym='ES'; ConId=495512563; Start='20250915 00:00:00'; End='20251219 23:59:00'; Expired=$true  },
    @{ Tag='ESH6'; Sym='ES'; ConId=649180695; Start='20251215 00:00:00'; End='20260320 23:59:00'; Expired=$true  },
    @{ Tag='ESM6'; Sym='ES'; ConId=649180678; Start='20260316 00:00:00'; End='20260618 23:59:00'; Expired=$true  },
    @{ Tag='ESU6'; Sym='ES'; ConId=649180671; Start='20260615 00:00:00'; End='20260906 12:00:00'; Expired=$false }
)

if (-not (Test-Path $Exe)) { Write-Host "找不到 exe: $Exe"; exit 1 }
New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null

foreach ($j in $jobs) {
    $tag = $j.Tag
    $segDir = Join-Path $OutRoot $tag
    New-Item -ItemType Directory -Path $segDir -Force | Out-Null

    $t = [datetime]::ParseExact($j.Start, 'yyyyMMdd HH:mm:ss', $null)
    $tEnd = [datetime]::ParseExact($j.End, 'yyyyMMdd HH:mm:ss', $null)

    # 枚举段边界：每 10 天一段
    $segments = @()
    $s = $t
    while ($s -lt $tEnd) {
        $e = $s.AddDays(10)
        if ($e -gt $tEnd) { $e = $tEnd }
        $segments += ,@($s, $e)
        $s = $e
    }

    Write-Host "==== $tag : $($segments.Count) 段 ($($j.Start) -> $($j.End)) ===="

    $failed = @()
    $done = 0
    $i = 0
    foreach ($seg in $segments) {
        $i++
        $s0 = $seg[0]; $e0 = $seg[1]
        $sStr = $s0.ToString('yyyyMMdd')
        $eStr = ($e0.AddMinutes(-1)).ToString('yyyyMMdd')   # 段文件命名用最后一天
        $base = "${tag}_1m_${sStr}_$eStr"
        $csv = Join-Path $segDir "$base.csv"
        $log = Join-Path $segDir "$base.log"

        if (Test-Path $csv) { Write-Host "  [$i/$($segments.Count)] 跳过(已有): $base.csv"; continue }

        $startArg = $s0.ToString('yyyyMMdd HH:mm:ss')
        $endArg = $e0.ToString('yyyyMMdd HH:mm:ss')
        $incExp = if ($j.Expired) { '--include-expired 1' } else { '' }

        Write-Host "  [$i/$($segments.Count)] $startArg .. $endArg"
        $ok = $false
        for ($try = 1; $try -le 3; $try++) {
            Write-Host "    尝试 $try/3 ..."
            Invoke-Expression "& '$Exe' --symbol $($j.Sym) --sectype FUT --exchange CME --currency USD --conid $($j.ConId) --client-id $ClientId --bar-size '1 min' --what-to-show TRADES --start '$startArg' --end '$endArg' --duration '30 D' --request-timeout 90 $incExp --output '$csv' *>> '$log'"
            $code = $LASTEXITCODE
            if ($code -eq 0 -and (Test-Path $csv)) {
                Write-Host "    ✓ $base.csv"
                $ok = $true
                $done++
                break
            }
            Write-Host "    ✗ exit=$code"
            if ($try -lt 3) { Start-Sleep -Seconds 60 }
        }
        if (-not $ok) { $failed += $base }
    }

    if ($failed.Count -eq 0) {
        # ---- 合并段文件 ----
        $mergedName = "${tag}_1m_$($t.ToString('yyyyMMdd'))_$($tEnd.ToString('yyyyMMdd'))"
        if ($tEnd.Hour -ne 0 -or $tEnd.Minute -ne 0) { $mergedName += "_$($tEnd.ToString('HHmm'))" }
        $merged = Join-Path $OutRoot "$mergedName.csv"
        $segFiles = Get-ChildItem $segDir -Filter "${tag}_1m_*.csv" | Sort-Object Name
        $outLines = New-Object System.Collections.Generic.List[string]
        $hdr = $null
        foreach ($sf in $segFiles) {
            $lines = Get-Content $sf.FullName
            if ($hdr -eq $null -and $lines.Count -gt 0) { $hdr = $lines[0] }
            for ($k = 1; $k -lt $lines.Count; $k++) { $outLines.Add($lines[$k]) }
        }
        if ($hdr -ne $null) {
            $all = New-Object System.Collections.Generic.List[string]
            $all.Add($hdr)
            $all.AddRange($outLines)
            [System.IO.File]::WriteAllLines($merged, $all)
            Write-Host "  ==> 合并完成: $merged ($($outLines.Count) 行)"
        }
    } else {
        Write-Host "  !! $tag 有 $($failed.Count) 段失败，未合并: $($failed -join ', ')"
    }
}

Write-Host ""
Write-Host "全部合约处理完毕（失败段如上，重跑本脚本即可续补）"
