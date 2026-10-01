# concat_csv.ps1 — 把目录下按名称排序的 CSV 合并成单个文件（只复制数据行，header 只保留一份）
#
# 用字节级块拷贝实现（12GB 级数据也能分钟级完成），不做逐行解析。

param(
    [Parameter(Mandatory=$true)][string]$InDir,
    [Parameter(Mandatory=$true)][string]$OutFile,
    [string]$Filter = '*.csv'
)

$files = Get-ChildItem $InDir -Filter $Filter | Sort-Object Name
if ($files.Count -eq 0) { Write-Host "目录中没有匹配文件: $InDir"; exit 1 }

Write-Host ("合并 {0} 个文件 -> {1}" -f $files.Count, $OutFile)
$t0 = Get-Date
$out = [System.IO.File]::Create($OutFile)
$buf = New-Object byte[] (4MB)
$one = New-Object byte[] 1
$i = 0
try {
    foreach ($f in $files) {
        $i++
        $fs = [System.IO.File]::OpenRead($f.FullName)
        try {
            if ($i -eq 1) {
                # 第一个文件：整份复制（含 header）
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $out.Write($buf, 0, $n) }
            } else {
                # 其余文件：跳过首行（header）后再块拷贝
                while (($n = $fs.Read($one, 0, 1)) -gt 0) { if ($one[0] -eq 10) { break } }
                while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $out.Write($buf, 0, $n) }
            }
        } finally { $fs.Dispose() }
        if ($i % 200 -eq 0) {
            Write-Host ("  .. {0}/{1}  ({2:N1} GB 已写)" -f $i, $files.Count, ($out.Length / 1GB))
        }
    }
} finally { $out.Dispose() }

$sz = (Get-Item $OutFile).Length / 1GB
Write-Host ("完成: {0}  ({1:N2} GB, 用时 {2:N1} 分钟)" -f $OutFile, $sz, ((Get-Date) - $t0).TotalMinutes)
