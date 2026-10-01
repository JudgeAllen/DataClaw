# fix_5s_gaps.ps1 — 按逐日差异清单补齐 5 秒数据（早段 00:00-05:59 或整日）
#
# 输入: data\5s_gap_days.csv（由逐日对比生成：Pair/Day/NQ/ES/TagNQ/TagES/ConNQ/ConES）
# 输出: <TAG>_5s_<YYYYMMDD>_early.csv（补 6 小时早段）或 _full.csv（整日）
#       重新合并由 fetch_5s_all.ps1 重跑完成

param(
    [string]$GapCsv = 'F:\Git\DataClaw\data\5s_gap_days.csv',
    [string]$OutRoot = 'F:\Git\DataClaw\data\bars_5s',
    [int]$ClientId = 88,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe',
    [int]$FullThreshold = 1000   # 行数低于此值 => 整天补
)

$gaps = Import-Csv $GapCsv
$done = 0; $skip = 0; $fail = @()

foreach ($g in $gaps) {
    $day = $g.Day.Replace('-', '')
    foreach ($side in @(@('NQ', $g.TagNQ, [int]$g.ConNQ, [int]$g.NQ), @('ES', $g.TagES, [int]$g.ConES, [int]$g.ES))) {
        $sym = $side[0]; $tag = $side[1]; $conid = $side[2]; $cnt = $side[3]
        $exp = 16560
        if ($cnt -ge $exp - 200) { continue }        # 已完整
        $full = ($cnt -lt $FullThreshold)
        $suffix = if ($full) { 'full' } else { 'early' }
        $out = Join-Path $OutRoot "$tag\${tag}_5s_${day}_${suffix}.csv"
        if (Test-Path $out) { $skip++; continue }
        $endStr = if ($full) { "$day 23:59:59" } else { "$day 05:59:59" }
        $log = Join-Path $OutRoot "$tag\${tag}_5s_${day}_${suffix}.log"
        $ok = $false
        for ($try = 1; $try -le 3; $try++) {
            & $Exe --symbol $sym --sectype FUT --exchange CME --currency USD --conid $conid `
                --client-id $ClientId --bar-size '5 secs' --what-to-show TRADES `
                --start "$day 00:00:00" --end $endStr --duration '1 D' --request-timeout 90 `
                --output $out *>> $log
            if ($LASTEXITCODE -eq 0) { $ok = $true; break }
            if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
            if ($try -lt 3) { Start-Sleep -Seconds 20 }
        }
        $n = if (Test-Path $out) { (Get-Content $out | Measure-Object -Line).Lines - 1 } else { 0 }
        if ($ok) { Write-Host ("  $tag $day [$suffix] 补 $n 根 (原 $cnt)"); $done++ }
        else { Write-Host ("  $tag $day [$suffix] 失败"); $fail += "$tag $day" }
    }
}
Write-Host "补拉完成: $done 项，跳过 $skip，失败 $($fail.Count)"
if ($fail.Count -gt 0) { $fail | ForEach-Object { Write-Host "  失败: $_" } }
