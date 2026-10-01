# check_ticks_integrity.ps1 — tick 数据内容完整性检查（只读）
#
# 检查项:
#   1. EMPTY      : 文件无数据行
#   2. HUGE       : 行数 >= 450,000（接近 max-pages 500 页上限，疑似被截断）
#   3. GAP_TAIL   : 末行时间距该小时结束 > 20 分钟（疑似尾部缺失）
#   4. GAP_HEAD   : 首行时间距该小时开始 > 20 分钟（疑似头部缺失）
# 说明: 清淡时段整小时无成交属正常，需结合 TRADES 交叉判断（见输出报告）

param(
    [string]$BaDir   = 'F:\Git\DataClaw\data\nqu6_ticksba_hours',
    [string]$TrDir   = 'F:\Git\DataClaw\data\nqu6_ticks_hours',
    [string]$Report  = 'F:\Git\DataClaw\data\integrity_report.csv'
)

$cs = @'
using System;
using System.IO;
using System.Text;
public static class TicksScan {
    public static long Lines; public static string First; public static string Last;
    public static void Scan(string path) {
        Lines = 0; First = null; Last = null;
        using (var r = new StreamReader(path, Encoding.UTF8, false, 1 << 20)) {
            string line = r.ReadLine(); // header
            while ((line = r.ReadLine()) != null) {
                if (line.Length < 19) continue;
                Lines++;
                if (First == null) First = line.Substring(0, 19);
                Last = line.Substring(0, 19);
            }
        }
    }
}
'@
if (-not ('TicksScan' -as [type])) { Add-Type -TypeDefinition $cs -Language CSharp }

function Scan-Dir($dir, $label) {
    $rows = New-Object System.Collections.Generic.List[object]
    $files = Get-ChildItem $dir -Filter '*.csv'
    $i = 0
    foreach ($f in $files) {
        $i++
        if ($i % 250 -eq 0) { Write-Host "  [$label] $i/$($files.Count) ..." }
        try {
            [TicksScan]::Scan($f.FullName)
            $rows.Add([pscustomobject]@{
                Dir = $label; File = $f.Name; Lines = [TicksScan]::Lines
                First = [TicksScan]::First; Last = [TicksScan]::Last
                KB = [math]::Round($f.Length / 1KB, 1)
            })
        } catch {
            $rows.Add([pscustomobject]@{ Dir=$label; File=$f.Name; Lines=-1; First='ERR'; Last=$_.Exception.Message; KB=0 })
        }
    }
    return $rows
}

Write-Host '扫描 BID_ASK ...'
$ba = Scan-Dir $BaDir 'BA'
Write-Host '扫描 TRADES ...'
$tr = Scan-Dir $TrDir 'TR'

$all = $ba + $tr
$all | Export-Csv -Path $Report -NoTypeInformation -Encoding UTF8
Write-Host "报告已写入: $Report"

# ---- 摘要与异常 ----
foreach ($set in @(@{Name='BID_ASK'; Rows=$ba}, @{Name='TRADES'; Rows=$tr})) {
    $r = $set.Rows
    $empty = $r | Where-Object { $_.Lines -eq 0 }
    $err   = $r | Where-Object { $_.Lines -lt 0 }
    $huge  = $r | Where-Object { $_.Lines -ge 450000 }
    $gapTail = @(); $gapHead = @()
    foreach ($x in $r) {
        if ($x.Lines -le 0 -or -not $x.File -match '_(\d{8})_(\d{2})\.csv$') { continue }
        $hh = [int]$matches[2]
        $day = $matches[1]
        $expStart = [datetime]::ParseExact("$day $('{0:D2}' -f $hh):00:00", 'yyyyMMdd HH:mm:ss', $null)
        $expEnd   = $expStart.AddMinutes(59).AddSeconds(59)
        try {
            $first = [datetime]::ParseExact($x.First, 'yyyy-MM-dd HH:mm:ss', $null)
            $last  = [datetime]::ParseExact($x.Last,  'yyyy-MM-dd HH:mm:ss', $null)
            if (($expStart - $first).TotalMinutes -gt 20) { $gapHead += $x }
            if (($expEnd - $last).TotalMinutes -gt 20)   { $gapTail += $x }
        } catch { }
    }
    Write-Host ""
    Write-Host "==== $($set.Name) ===="
    Write-Host "文件数: $($r.Count) | 空文件: $($empty.Count) | 读取错误: $($err.Count) | 超大(>=45万行): $($huge.Count) | 头部缺口: $($gapHead.Count) | 尾部缺口: $($gapTail.Count)"
    if ($empty.Count -gt 0) { Write-Host "  空文件: $(($empty | Select-Object -First 10).File -join ', ')" }
    if ($huge.Count -gt 0)  { Write-Host "  超大文件:"; $huge | ForEach-Object { Write-Host "    $($_.File) : $($_.Lines) 行" } }
    if ($gapHead.Count -gt 0) { Write-Host "  头部缺口(前10):"; $gapHead | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.File) first=$($_.First) lines=$($_.Lines)" } }
    if ($gapTail.Count -gt 0) { Write-Host "  尾部缺口(前10):"; $gapTail | Select-Object -First 10 | ForEach-Object { Write-Host "    $($_.File) last=$($_.Last) lines=$($_.Lines)" } }
}

# ---- BA vs TRADES 交叉验证 ----
Write-Host ""
Write-Host "==== 交叉验证（同小时 TRADES 活跃但 BA 极小）===="
$baMap = @{}; foreach ($x in $ba) { if ($x.File -match 'NQU6_TICKSBA_(\d{8})_(\d{2})\.csv$') { $baMap["$($matches[1])_$($matches[2])"] = $x } }
$trMap = @{}; foreach ($x in $tr) { if ($x.File -match 'NQU6_TICKS_(\d{8})_(\d{2})\.csv$')    { $trMap["$($matches[1])_$($matches[2])"] = $x } }
$suspect = @()
foreach ($k in $baMap.Keys) {
    if (-not $trMap.ContainsKey($k)) { continue }
    $b = $baMap[$k]; $t = $trMap[$k]
    if ($t.Lines -ge 500 -and $b.Lines -lt 50) { $suspect += [pscustomobject]@{ Hour=$k; TR=$t.Lines; BA=$b.Lines } }
}
if ($suspect.Count -eq 0) { Write-Host "无异常（TRADES 活跃的小时 BA 行数均正常）" }
else { $suspect | Sort-Object Hour | ForEach-Object { Write-Host "  $($_.Hour): TRADES $($_.TR) 行 / BA $($_.BA) 行" } }
