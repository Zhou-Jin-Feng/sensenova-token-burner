#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = '7.1.0'
$sha256 = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$toolDirectory = Join-Path $repoRoot '.local\tools\inno-7.1.0'
$compiler = Join-Path $toolDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
$downloads = Join-Path $repoRoot '.local\downloads'
$null = New-Item -ItemType Directory -Path $downloads -Force
$installer = Join-Path $downloads "innosetup-$version-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-$version-x64.exe" -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256) {
    throw 'Inno Setup 官方下载 SHA-256 不匹配。'
}
if ((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') {
    throw 'Inno Setup 官方安装器签名未验证。'
}
# 官方portable模式不创建卸载注册项，/NOICONS避免快捷方式，仅准备项目内编译工具。
$start = [Diagnostics.ProcessStartInfo]::new($installer)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/portable=1','/CURRENTUSER','/NOICONS',"/DIR=$toolDirectory")) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw '项目内安装器编译工具准备失败。' }
} finally { $process.Dispose() }
Write-Output $compiler
