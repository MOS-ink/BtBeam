# build.ps1 — BtBeam 统一编译脚本（含 WinRT 引用 + 签名 + 部署）
param(
    [switch]$NoSign,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "  === BtBeam Build ===" -ForegroundColor Cyan
Write-Host ""

$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { Write-Host "  [X] 找不到 csc.exe" -ForegroundColor Red; exit 1 }

# WinRT 引用
$kitsRef = "C:\Program Files (x86)\Windows Kits\10\References\10.0.26100.0"
$refs = @(
    "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\Facade\Windows.WinMD",
    "$kitsRef\Windows.Foundation.UniversalApiContract\19.0.0.0\Windows.Foundation.UniversalApiContract.winmd",
    "$kitsRef\Windows.Foundation.FoundationContract\4.0.0.0\Windows.Foundation.FoundationContract.winmd",
    "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll",
    "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades\System.Runtime.dll"
)
foreach ($r in $refs) {
    if (-not (Test-Path $r)) { Write-Host "  [X] 缺少引用: $r" -ForegroundColor Red; exit 1 }
}
Write-Host "  [1/4] WinRT 引用齐全" -ForegroundColor Green

$argList = @(
    '/nologo', '/target:winexe', '/optimize+',
    '/win32manifest:BtBeam.manifest',
    '/win32icon:BtBeam.ico',
    '/out:BtBeam.exe'
) + ($refs | ForEach-Object { "/r:$_" }) + @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    'BtBeam.cs'
)

# 先杀掉运行中的实例（否则文件被占用）
Get-Process BtBeam -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "  [2/4] 编译 ..." -ForegroundColor Yellow
$out = & $csc @argList 2>&1 | Out-String
$errs = $out -split "`r?`n" | Where-Object { $_ -match 'error ' }
if ($LASTEXITCODE -ne 0 -or $errs.Count -gt 0) {
    Write-Host "  [X] 编译失败" -ForegroundColor Red
    $errs | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    exit 1
}
$kb = [math]::Round((Get-Item .\BtBeam.exe).Length / 1KB, 1)
Write-Host "  [2/4] 编译成功  BtBeam.exe  $kb KB" -ForegroundColor Green

# 签名
if (-not $NoSign) {
    Write-Host "  [3/4] 签名 ..." -ForegroundColor Yellow
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq 'CN=BtBeam' } | Select-Object -First 1
    if ($cert) {
        [void](Set-AuthenticodeSignature -FilePath (Resolve-Path .\BtBeam.exe).Path -Certificate $cert -HashAlgorithm SHA256)
        Write-Host "  [3/4] 签名完成" -ForegroundColor Green
    } else {
        Write-Host "  [3/4] 没有证书，跳过（先跑一次 sign.ps1）" -ForegroundColor DarkYellow
    }
}

# 部署
if (-not $NoDeploy) {
    Write-Host "  [4/4] 部署 ..." -ForegroundColor Yellow
    $dest = "$env:LOCALAPPDATA\BtBeam"
    if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
    Copy-Item .\BtBeam.exe "$dest\BtBeam.exe" -Force
    Copy-Item .\BtBeam.exe "$env:USERPROFILE\Desktop\BtBeam.exe" -Force
    Write-Host "        -> $dest\BtBeam.exe" -ForegroundColor Green
    Write-Host "        -> $env:USERPROFILE\Desktop\BtBeam.exe" -ForegroundColor Green
} else {
    Write-Host "  [4/4] 跳过部署" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  完成" -ForegroundColor Cyan
Write-Host ""
