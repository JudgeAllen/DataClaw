# auto_resume_fix.ps1 — 等待 IB 历史数据恢复后自动拉起 3 个补丁分片
#
# 每 3 分钟探测一次历史 tick 接口；成功即启动 3 个分片（Shards=3）后退出。
# 用于 ushmds 农场故障（code 2105 / 请求超时）期间的无人值守恢复。

param(
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe',
    [string]$Script = 'F:\Git\DataClaw\HistoryTool\fix_truncated.ps1',
    [int]$IntervalSec = 180,
    [int]$MaxHours = 48
)

$probe = 'F:\Git\DataClaw\data\_probe_auto.csv'
$deadline = (Get-Date).AddHours($MaxHours)
$n = 0

while ((Get-Date) -lt $deadline) {
    $n++
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
    & $Exe --ticks --symbol NQ --sectype FUT --exchange CME --currency USD --conid 770561204 `
        --client-id 89 --what-to-show BID_ASK --start '20260910 21:00:00' --end '20260910 21:00:15' `
        --request-timeout 40 --output $probe *> $null

    $ok = $false
    if (Test-Path $probe) {
        if ((Get-Content $probe | Measure-Object -Line).Lines -gt 1) { $ok = $true }
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
    }

    if ($ok) {
        Write-Host "[$(Get-Date -Format 'MM-dd HH:mm:ss')] 历史数据已恢复（第 $n 次探测），启动 3 个分片"
        for ($i = 0; $i -lt 3; $i++) {
            Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
                '-NoProfile','-ExecutionPolicy','Bypass','-File',$Script,
                '-Mode','BA','-Shard',"$i",'-Shards','3','-ClientId',"$($90 + $i)"
            )
            Start-Sleep -Seconds 5
        }
        Write-Host "已启动 3 个分片，探测脚本退出"
        exit 0
    }

    Write-Host "[$(Get-Date -Format 'MM-dd HH:mm:ss')] 第 $n 次探测仍失败（ushmds 未恢复），$IntervalSec 秒后重试"
    Start-Sleep -Seconds $IntervalSec
}

Write-Host "超过 $MaxHours 小时仍未恢复，探测退出"
exit 1
