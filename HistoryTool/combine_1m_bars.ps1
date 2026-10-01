# combine_1m_bars.ps1 — 把每个合约的 1m K 线分段合并为「一个合约一个文件」
#
# - 排除 U6 的 _2359 版本（含 09-15 日盘，定稿截止 09-14 交易日）
# - 全量去重（分段边界重复 bar）+ 按时间排序
# - 输出 <TAG>_1m_<首日>_<末日>.csv；原始分段文件保留不动

param(
    [string]$Root = 'F:\Git\DataClaw\data\bars_1m'
)

$skip = @('NQU6_1m_20260905_20260915_2359.csv', 'ESU6_1m_20260905_20260915_2359.csv')
$tags = @('NQZ5','NQH6','NQM6','NQU6','ESZ5','ESH6','ESM6','ESU6')
$hdr = 'time,open,high,low,close,volume,wap,count'

foreach ($tag in $tags) {
    $files = Get-ChildItem $Root -Filter "${tag}_1m_*.csv" |
        Where-Object { $skip -notcontains $_.Name -and $_.Name -notmatch '_combined\.csv$' } |
        Sort-Object Name
    if ($files.Count -eq 0) { Write-Host "$tag : 无文件，跳过"; continue }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $rows = New-Object 'System.Collections.Generic.List[string]'
    foreach ($f in $files) {
        $first = $true
        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            if ($first) { $first = $false; continue }
            if ($line.Length -lt 19) { continue }
            if ($seen.Add($line)) { $rows.Add($line) }
        }
    }
    if ($rows.Count -eq 0) { Write-Host "$tag : 无数据行，跳过"; continue }

    $rows.Sort()   # 定宽时间戳前缀 -> 字符串序 = 时间序
    $firstDay = $rows[0].Substring(0,10) -replace '-',''
    $lastDay  = $rows[$rows.Count-1].Substring(0,10) -replace '-',''
    $out = Join-Path $Root "${tag}_1m_${firstDay}_${lastDay}.csv"

    $all = New-Object 'System.Collections.Generic.List[string]'
    $all.Add($hdr); $all.AddRange($rows)
    [System.IO.File]::WriteAllLines($out, $all)

    Write-Host ("{0,-5}: {1} 段 -> {2,7} 行  {3} .. {4}  -> {5}" -f `
        $tag, $files.Count, $rows.Count, $rows[0].Substring(0,16), $rows[$rows.Count-1].Substring(0,16), (Split-Path $out -Leaf))
}
