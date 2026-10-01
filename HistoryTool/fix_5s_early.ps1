# fix_5s_early.ps1 — 补齐 5 秒数据的每日早段（00:00-06:59）
#
# 背景: 5s 拉取的段窗口从各合约的开盘时刻(06:00/07:00)起，导致每日早段
#       （属前一交易日尾盘，冬令时下为 00:00-06:59）长期缺失。
# 做法: 扫描每个合约的合并文件，找出"工作日行数明显不足"的日期，
#       逐日补拉 [00:00:00, 06:59:59] 存为 <TAG>_5s_<date>_early2.csv（不覆盖已有文件）。
# 之后由 fetch_5s_all.ps1 重跑完成重新合并。

param(
    [string]$OutRoot = 'F:\Git\DataClaw\data\bars_5s',
    [int]$ClientId = 88,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

$cs = @'
using System; using System.Collections.Generic; using System.IO;
public static class DayScan6 {
  public static Dictionary<string,long> Days(string path) {
    var d = new Dictionary<string,long>();
    using (var r = new StreamReader(path)) {
      r.ReadLine(); string l;
      while ((l = r.ReadLine()) != null) {
        if (l.Length < 10) continue;
        var k = l.Substring(0,10);
        long v; if (d.TryGetValue(k, out v)) d[k] = v + 1; else d[k] = 1;
      }
    }
    return d;
  }
}
'@
if (-not ('DayScan6' -as [type])) { Add-Type -TypeDefinition $cs -Language CSharp }

$jobs = @(
    @{ Tag='NQZ5'; Sym='NQ'; ConId=563947738 }, @{ Tag='NQH6'; Sym='NQ'; ConId=730283097 },
    @{ Tag='NQM6'; Sym='NQ'; ConId=750150196 }, @{ Tag='NQU6'; Sym='NQ'; ConId=770561204 },
    @{ Tag='ESZ5'; Sym='ES'; ConId=495512563 }, @{ Tag='ESH6'; Sym='ES'; ConId=649180695 },
    @{ Tag='ESM6'; Sym='ES'; ConId=649180678 }, @{ Tag='ESU6'; Sym='ES'; ConId=649180671 }
)

$total = 0; $fail = @()
foreach ($j in $jobs) {
    $tag = $j.Tag
    $merged = Get-ChildItem $OutRoot -Filter "${tag}_5s_*.csv" | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $merged) { continue }
    $days = [DayScan6]::Days($merged.FullName)
    $todo = @()
    foreach ($k in ($days.Keys | Sort-Object)) {
        $dt = [datetime]::ParseExact($k, 'yyyy-MM-dd', $null)
        $cnt = $days[$k]
        if ($dt.DayOfWeek -eq 'Sunday') { continue }
        if ($dt.DayOfWeek -eq 'Monday' -and $cnt -ge 12000) { continue }        # 周一从早/晚 06:00/07:00 开始，12,240~12,960 即完整
        if ($dt.DayOfWeek -eq 'Saturday' -and $cnt -ge 3400) { continue }        # 周六凌晨约 3,600
        if ($dt.DayOfWeek -ne 'Saturday' -and $cnt -ge 15000) { continue }       # 工作日已完整
        if ($cnt -eq 0) { continue }
        $todo += $k
    }
    if ($todo.Count -eq 0) { Write-Host "$tag : 无早段缺口"; continue }
    Write-Host ("{0} : 需补早段 {1} 天" -f $tag, $todo.Count)
    foreach ($k in $todo) {
        $day = $k.Replace('-', '')
        # IB 的 5 秒 bar 按交易日组织：跨交易日的窗口只会返回新交易日的部分。
        # 因此补"某日早段(00:00-05:59，属前一交易日)"必须用同一交易日内的窗口：
        #   [前一日 23:00, 当日 05:59]
        $prev = ([datetime]::ParseExact($k, 'yyyy-MM-dd', $null)).AddDays(-1).ToString('yyyyMMdd')
        $out = Join-Path $OutRoot "$tag\${tag}_5s_${day}_early3.csv"
        if (Test-Path $out) { continue }
        $log = Join-Path $OutRoot "$tag\${tag}_5s_${day}_early3.log"
        $ok = $false
        for ($try = 1; $try -le 3; $try++) {
            & $Exe --symbol $j.Sym --sectype FUT --exchange CME --currency USD --conid $j.ConId `
                --client-id $ClientId --bar-size '5 secs' --what-to-show TRADES `
                --start "$prev 23:00:00" --end "$day 05:59:59" --duration '2 D' --request-timeout 90 `
                --output $out *>> $log
            if ($LASTEXITCODE -eq 0) { $ok = $true; break }
            if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }
            if ($try -lt 3) { Start-Sleep -Seconds 20 }
        }
        $n = if (Test-Path $out) { (Get-Content $out | Measure-Object -Line).Lines - 1 } else { 0 }
        if ($ok) { $total++ } else { $fail += "$tag $day" }
    }
}
Write-Host "早段补拉完成: $total 项成功，失败 $($fail.Count)"
if ($fail.Count -gt 0) { $fail | ForEach-Object { Write-Host "  失败: $_" } }
