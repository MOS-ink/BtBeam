# sign.ps1 — 给 BtBeam.exe 打自签名证书，消除"未知发布者"提示
# 不需要管理员权限，全部操作在当前用户证书存储内完成

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'BtBeam.exe'

if (-not (Test-Path $exe)) { Write-Host "[X] 找不到 $exe" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "  BtBeam 代码签名" -ForegroundColor Cyan
Write-Host "  ----------------------------------------" -ForegroundColor DarkGray

# 1. 找到或创建代码签名证书
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq 'CN=BtBeam' } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if ($cert) {
    Write-Host "  [1/4] 复用已有证书  指纹 $($cert.Thumbprint.Substring(0,16))..." -ForegroundColor Green
} else {
    Write-Host "  [1/4] 创建自签名证书..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject 'CN=BtBeam' `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host "        指纹 $($cert.Thumbprint)" -ForegroundColor Green
}

# 2. 签名
Write-Host "  [2/4] 签名 BtBeam.exe ..." -ForegroundColor Yellow
Get-Process BtBeam -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid') {
    Write-Host "        签名状态: $($sig.Status)（自签名通常显示 UnknownError，属正常）" -ForegroundColor DarkYellow
} else {
    Write-Host "        签名完成" -ForegroundColor Green
}

# 3. 导出证书
$cerPath = Join-Path $env:TEMP 'BtBeam.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "  [3/4] 已导出证书 -> $cerPath" -ForegroundColor Green

# 4. 导入到当前用户的受信任存储
Write-Host "  [4/4] 导入受信任存储（可能弹一次安全确认，点『是』）..." -ForegroundColor Yellow
try {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
    Write-Host "        已加入 受信任的发布者" -ForegroundColor Green
} catch { Write-Host "        受信任发布者导入失败: $($_.Exception.Message)" -ForegroundColor DarkYellow }

try {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
    Write-Host "        已加入 受信任的根证书颁发机构" -ForegroundColor Green
} catch { Write-Host "        根证书导入失败: $($_.Exception.Message)" -ForegroundColor DarkYellow }

Write-Host ""
Write-Host "  ----------------------------------------" -ForegroundColor DarkGray
$v = (Get-AuthenticodeSignature $exe)
Write-Host "  签名者: $($v.SignerCertificate.Subject)"
Write-Host "  状态  : $($v.Status)"
Write-Host ""
Write-Host "  现在双击 BtBeam.exe，SmartScreen 应该不再拦截。" -ForegroundColor Cyan
Write-Host "  若仍提示，说明 SmartScreen 走的是云端信誉判断，需要联网一段时间后才消除。" -ForegroundColor DarkGray
Write-Host ""
