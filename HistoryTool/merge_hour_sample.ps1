# merge_hour_sample.ps1 — 单小时 TRADES+BA 合并为 tick 事件流（样例/原型）
#
# 用法: .\merge_hour_sample.ps1 -TradesFile <t.csv> -BaFile <ba.csv> -OutFile <out.csv>
#
# 输出列: time,type,last_price,last_size,bid_price,bid_size,ask_price,ask_size
#   type=T 成交事件（last 为本笔，bid/ask 为当时已知盘口）
#   type=Q 盘口事件（bid/ask 为本笔，last 为当时已知最后成交）
# 前向填充仅基于本文件内数据；时间字符串定宽，直接按字典序比较（=时间序）

param(
    [string]$TradesFile,
    [string]$BaFile,
    [string]$OutFile
)

if (-not (Test-Path $TradesFile)) { Write-Host "缺 TRADES 文件: $TradesFile"; exit 1 }
if (-not (Test-Path $BaFile)) { Write-Host "缺 BA 文件: $BaFile"; exit 1 }

$rT = [System.IO.StreamReader]::new($TradesFile)
$rB = [System.IO.StreamReader]::new($BaFile)
$w = [System.IO.StreamWriter]::new($OutFile, $false, [System.Text.UTF8Encoding]::new($false))
try {
    $w.WriteLine('time,type,last_price,last_size,bid_price,bid_size,ask_price,ask_size')
    $null = $rT.ReadLine(); $null = $rB.ReadLine()   # headers
    $lineT = $rT.ReadLine()
    $lineB = $rB.ReadLine()

    $lastPx = ''; $lastSz = ''
    $bidPx = ''; $bidSz = ''; $askPx = ''; $askSz = ''

    $n = 0
    while ($lineT -or $lineB) {
        $takeT = $false
        if ($lineT -and $lineB) {
            # 定宽时间串直接比较；同秒 T 先于 Q
            $takeT = ([string]::CompareOrdinal($lineT, 0, $lineB, 0, 19) -le 0)
        } elseif ($lineT) { $takeT = $true }

        if ($takeT) {
            $p = $lineT.Split(',')
            $lastPx = $p[1]; $lastSz = $p[2]
            $w.Write($p[0]); $w.Write(',T,'); $w.Write($p[1]); $w.Write(','); $w.Write($p[2])
            $w.Write(",${bidPx},${bidSz},${askPx},${askSz}"); $w.WriteLine('')
            $lineT = $rT.ReadLine()
        } else {
            $p = $lineB.Split(',')
            $bidPx = $p[1]; $bidSz = $p[2]; $askPx = $p[3]; $askSz = $p[4]
            $w.Write($p[0]); $w.Write(",Q,${lastPx},${lastSz},"); $w.Write($p[1]); $w.Write(','); $w.Write($p[2])
            $w.Write(','); $w.Write($p[3]); $w.Write(','); $w.WriteLine($p[4])
            $lineB = $rB.ReadLine()
        }
        $n++
        if ($n % 20000 -eq 0) { Write-Host "  $n 行..." }
    }
    Write-Host "合并完成: $n 行 -> $OutFile"
}
finally {
    $rT.Dispose(); $rB.Dispose(); $w.Dispose()
}
