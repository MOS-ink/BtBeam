# send-to-hotspot.ps1 —— 模拟 BtBeam 发送方的热点通道发文件
# 用法: -Dest 192.168.137.1 -Port 53321 -File <path>
param(
    [string]$Dest = "192.168.137.1",
    [int]$Port = 53321,
    [string]$File = "",
    [int]$ChunkKB = 256
)

if (-not (Test-Path $File)) { Write-Host "NO_FILE $File"; exit 1 }

$fi = Get-Item $File
$name = $fi.Name
$size = $fi.Length

$client = New-Object System.Net.Sockets.TcpClient
try {
    $client.Connect($Dest, $Port)
} catch {
    Write-Host ("CONNECT_FAIL " + $_.Exception.Message)
    exit 1
}

$ns = $client.GetStream()
$ns.WriteTimeout = 30000

# 协议头: LBBT1 {"name":"...","size":N}\n
$header = 'LBBT1 {"name":"' + $name + '","size":' + $size + '}' + "`n"
$hb = [Text.Encoding]::UTF8.GetBytes($header)
$ns.Write($hb, 0, $hb.Length)

$sw = [Diagnostics.Stopwatch]::StartNew()
$buf = New-Object byte[] ($ChunkKB * 1024)
$fs = [IO.File]::OpenRead($File)
$sent = 0
try {
    while ($true) {
        $n = $fs.Read($buf, 0, $buf.Length)
        if ($n -le 0) { break }
        $ns.Write($buf, 0, $n)
        $sent += $n
    }
    $ns.Flush()
} finally {
    $fs.Close()
}
$sw.Stop()

$mb = $sent / 1MB
$sec = $sw.Elapsed.TotalSeconds
$rate = if ($sec -gt 0) { $mb / $sec } else { 0 }
Write-Host ("SENT {0}  {1:N1} MB  {2:N2}s  {3:N1} MB/s" -f $name, $mb, $sec, $rate)

# 读 ACK
$ns.ReadTimeout = 15000
$ab = New-Object byte[] 256
try {
    $an = $ns.Read($ab, 0, $ab.Length)
    if ($an -gt 0) {
        Write-Host ("ACK " + [Text.Encoding]::UTF8.GetString($ab, 0, $an).Trim())
    } else {
        Write-Host "ACK none"
    }
} catch {
    Write-Host ("ACK timeout: " + $_.Exception.Message)
}

$client.Close()
Write-Host "DONE"
