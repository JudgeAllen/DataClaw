# resample_second_sample.ps1 — 事件流 -> 秒级聚合（样例/原型，带成交量）
#
# 用法: .\resample_second_sample.ps1 -StreamFile <stream.csv> -OutFile <out.csv> `
#           -Start '2026-09-03 04:00:00' -End '2026-09-03 04:59:59'
#
# 输出列: time,last_price,last_size,volume,bid_price,bid_size,ask_price,ask_size
#   volume = 该秒内所有成交(type=T)笔 size 之和；无成交的秒为 0（不沿用）
# 语义:
#   - 输出 [Start, End] 内每秒一行（连续时间轴）
#   - 有事件的秒: last/bid/ask 取该秒最后一次事件后的状态（事件流行已前向填充）
#   - 无事件的秒: last/bid/ask 沿用前一秒状态，volume=0
#   - Start 之前的事件只作状态种子（不输出）

param(
    [string]$StreamFile,
    [string]$OutFile,
    [string]$Start,
    [string]$End
)

if (-not (Test-Path $StreamFile)) { Write-Host "缺事件流文件: $StreamFile"; exit 1 }
if (-not $Start -or -not $End) { Write-Host "需要 -Start 与 -End (yyyy-MM-dd HH:mm:ss)"; exit 1 }

$fmt = 'yyyy-MM-dd HH:mm:ss'
$startDt = [datetime]::ParseExact($Start, $fmt, $null)
$endDt = [datetime]::ParseExact($End, $fmt, $null)

$r = [System.IO.StreamReader]::new($StreamFile)
$w = [System.IO.StreamWriter]::new($OutFile, $false, [System.Text.UTF8Encoding]::new($false))
try {
    $null = $r.ReadLine()  # header
    $w.WriteLine('time,last_price,last_size,volume,bid_price,bid_size,ask_price,ask_size')

    $lastPx = ''; $lastSz = ''; $bidPx = ''; $bidSz = ''; $askPx = ''; $askSz = ''
    $clock = $startDt      # 输出游标
    $volSec = $null        # 正在累计的秒
    $vol = 0               # 当前秒累计成交量

    while ($null -ne ($line = $r.ReadLine())) {
        $sec = [datetime]::ParseExact($line.Substring(0, 19), $fmt, $null)
        $p = $line.Split(',')
        # 列: time,type,last_price,last_size,bid_price,bid_size,ask_price,ask_size

        if ($sec -lt $startDt) {
            # 种子：只更新状态（成交也进种子状态，但不计 volume——范围外）
            if ($p[1] -eq 'T') { $lastPx = $p[2]; $lastSz = $p[3] }
            else { $bidPx = $p[4]; $bidSz = $p[5]; $askPx = $p[6]; $askSz = $p[7] }
            continue
        }
        if ($sec -gt $endDt) { break }

        if ($sec -ne $volSec) {
            # 进入新秒：先结算上一秒（clock .. sec-1）
            while ($clock -lt $sec) {
                $v = 0
                if ($volSec -ne $null -and $clock -eq $volSec) { $v = $vol }
                $w.Write($clock.ToString($fmt))
                $w.Write(",$lastPx,$lastSz,$v,$bidPx,$bidSz,$askPx,$askSz")
                $w.WriteLine('')
                $clock = $clock.AddSeconds(1)
            }
            $volSec = $sec
            $vol = 0
        }

        if ($p[1] -eq 'T') {
            $lastPx = $p[2]; $lastSz = $p[3]
            $vol += [long]$p[3]
        } else {
            $bidPx = $p[4]; $bidSz = $p[5]; $askPx = $p[6]; $askSz = $p[7]
        }
    }

    # 收尾: 输出 clock .. endDt
    while ($clock -le $endDt) {
        $v = 0
        if ($volSec -ne $null -and $clock -eq $volSec) { $v = $vol }
        $w.Write($clock.ToString($fmt))
        $w.Write(",$lastPx,$lastSz,$v,$bidPx,$bidSz,$askPx,$askSz")
        $w.WriteLine('')
        $clock = $clock.AddSeconds(1)
    }
    Write-Host "秒级聚合完成 -> $OutFile"
}
finally {
    $r.Dispose(); $w.Dispose()
}
