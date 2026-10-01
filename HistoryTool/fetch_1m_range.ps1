# fetch_1m_range.ps1 — 拉取单个合约指定区间的 1m K 线（10 天/段，断点续传，自动合并）
#
# 用法:
#   .\fetch_1m_range.ps1 -Tag NQU6 -Sym NQ -ConId 770561204 -Start '20260603 00:00:00' -End '20260614 23:59:00'
#
# 输出:
#   段文件 <OutRoot>\<Tag>\<Tag>_1m_<起>_<止>.csv
#   合并   <OutRoot>\<Tag>_1m_<起日>_<止日>[_HHmm].csv

param(
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$Sym,
    [Parameter(Mandatory=$true)][int]$ConId,
    [Parameter(Mandatory=$true)][string]$Start,
    [Parameter(Mandatory=$true)][string]$End,
    [switch]$Expired,
    [string]$OutRoot = 'F:\Git\DataClaw\data\bars_1m',
    [int]$ClientId = 88,
    [string]$Exe = 'F:\Git\DataClaw\HistoryTool\bin\Release\net9.0\TwsHistory.exe'
)

if (-not (Test-Path $Exe)) { Write-Host "找不到 exe: $Exe"; exit 1 }
$segDir = Join-Path $OutRoot $Tag
New-Item -ItemType Directory -Path $segDir -Force | Out-Null

$t = [datetime]::ParseExact($Start, 'yyyyMMdd HH:mm:ss', $null)
$tEnd = [datetime]::ParseExact($End, 'yyyyMMdd HH:mm:ss', $null)

$segments = @()
$s = $t
while ($s -lt $tEnd) {
    $e = $s.AddDays(10); if ($e -gt $tEnd) { $e = $tEnd }
    $segments += ,@($s, $e)
    $s = $e
}

Write-Host "==== $Tag : $($segments.Count) 段 ($Start -> $End) ===="
$failed = @(); $done = 0; $noData = 0; $i = 0
foreach ($seg in $segments) {
    $i++
    $s0 = $seg[0]; $e0 = $seg[1]
    $base = "${Tag}_1m_$($s0.ToString('yyyyMMdd'))_$(($e0.AddMinutes(-1)).ToString('yyyyMMdd'))"
    $csv = Join-Path $segDir "$base.csv"
    $log = Join-Path $segDir "$base.log"
    if (Test-Path $csv) { Write-Host "  [$i/$($segments.Count)] 跳过(已有): $base.csv"; continue }

    $startArg = $s0.ToString('yyyyMMdd HH:mm:ss')
    $endArg = $e0.ToString('yyyyMMdd HH:mm:ss')
    $incExp = if ($Expired) { '--include-expired 1' } else { '' }
    Write-Host "  [$i/$($segments.Count)] $startArg .. $endArg"

    $ok = $false
    for ($try = 1; $try -le 3; $try++) {
        & $Exe --symbol $Sym --sectype FUT --exchange CME --currency USD --conid $ConId `
            --client-id $ClientId --bar-size '1 min' --what-to-show TRADES `
            --start $startArg --end $endArg --duration '30 D' --request-timeout 90 `
            --output $csv *>> $log
        $code = $LASTEXITCODE
        if ($code -eq 0) {
            if (Test-Path $csv) { Write-Host "    ✓ $base.csv"; $done++ }
            else { Write-Host "    - 该段无数据(周末/假日)"; $noData++ }
            $ok = $true
            break
        }
        Write-Host "    ✗ exit=$code"
        if ($try -lt 3) { Start-Sleep -Seconds 60 }
    }
    if (-not $ok) { $failed += $base }
}

if ($failed.Count -eq 0) {
    $mergedName = "${Tag}_1m_$($t.ToString('yyyyMMdd'))_$($tEnd.ToString('yyyyMMdd'))"
    if ($tEnd.Hour -ne 0 -or $tEnd.Minute -ne 0) { $mergedName += "_$($tEnd.ToString('HHmm'))" }
    $merged = Join-Path $OutRoot "$mergedName.csv"
    $segFiles = Get-ChildItem $segDir -Filter "${Tag}_1m_*.csv" |
        Where-Object { $_.Name -match "${Tag}_1m_(\d{8})_(\d{8})\.csv$" -and $matches[1] -ge $t.ToString('yyyyMMdd') -and $matches[1] -le $tEnd.ToString('yyyyMMdd') } |
        Sort-Object Name
    $outLines = New-Object System.Collections.Generic.List[string]
    $hdr = $null
    foreach ($sf in $segFiles) {
        $lines = Get-Content $sf.FullName
        if ($hdr -eq $null -and $lines.Count -gt 0) { $hdr = $lines[0] }
        for ($k = 1; $k -lt $lines.Count; $k++) { $outLines.Add($lines[$k]) }
    }
    if ($hdr -ne $null) {
        $all = New-Object System.Collections.Generic.List[string]
        $all.Add($hdr); $all.AddRange($outLines)
        [System.IO.File]::WriteAllLines($merged, $all)
        Write-Host "  ==> 合并完成: $merged ($($outLines.Count) 行，$($segFiles.Count) 段)"
    }
} else {
    Write-Host "  !! 有 $($failed.Count) 段失败，未合并: $($failed -join ', ')"
    exit 1
}
