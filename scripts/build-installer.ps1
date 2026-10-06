#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$')]
    [string]$Version = '0.1.0-preview.1',
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repoRoot '.local\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
}
if (-not $CompilerPath) { $CompilerPath = Join-Path $repoRoot '.local\tools\inno-7.1.0\ISCC.exe' }
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw '先运行 scripts/prepare-installer-tool.ps1，或指定已安装的 Inno Setup 7 编译器。' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$buildRoot = Join-Path $repoRoot ".local\releases\$Version-$runId"
$publishDirectory = Join-Path $buildRoot 'publish'
$outputDirectory = Join-Path $buildRoot 'artifacts'
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
$numericVersion = ($Version -split '-')[0] + '.0'
$environmentValues = @{
    DOTNET_ROOT = (Split-Path -Parent $dotnet)
    DOTNET_CLI_HOME = (Join-Path $repoRoot '.local\cli-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    NUGET_PACKAGES = (Join-Path $repoRoot '.local\nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $repoRoot '.local\nuget\http-cache')
}
$previous = @{}
foreach ($name in $environmentValues.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
    [Environment]::SetEnvironmentVariable($name,$environmentValues[$name],'Process')
}
Push-Location -LiteralPath $repoRoot
try {
    $expectedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    if ((& $dotnet --version | Out-String).Trim() -ne $expectedSdk) { throw "打包需要 SDK $expectedSdk。" }
    # 发布RID与开发锁不同，使用专用锁；仅首次生成，后续构建锁定还原。
    $releaseLocks = @('Core','Infrastructure','Desktop' | ForEach-Object { "src/SenseNova.TokenBurner.$_/packages.win-x64.lock.json" })
    $lockedRestore = @($releaseLocks | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0
    & $dotnet publish 'src/SenseNova.TokenBurner.Desktop/SenseNova.TokenBurner.Desktop.csproj' `
        --configuration Release --runtime win-x64 --self-contained true --output $publishDirectory `
        -p:RuntimeFrameworkVersion=10.0.12 -p:NuGetLockFilePath=packages.win-x64.lock.json "-p:RestoreLockedMode=$lockedRestore" `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None `
        "-p:Version=$Version" "-p:FileVersion=$numericVersion" *> (Join-Path $buildRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw "自包含发布失败，见 $buildRoot/publish.log。" }
    foreach ($required in @('SenseNova.TokenBurner.exe','coreclr.dll','hostfxr.dll','System.Windows.Forms.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required))) { throw "自包含发布缺少 $required。" }
    }
    $files = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Force)
    $invalid = @($files | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint -or $_.Extension -in @('.pdb','.log','.dat','.env','.cs','.ps1') `
        -or $_.Name -match '^(?:settings|mock-settings|tasks|run-state|api-key|mock-credential)\.'
    })
    if ($invalid.Count) { throw '发布目录包含禁止打包的文件。' }
    if (@(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Directory -Force | Where-Object Name -In @('agent','.local','user-data')).Count) {
        throw '发布目录混入私有目录。'
    }
    Copy-Item -LiteralPath 'docs\首次使用.md' -Destination (Join-Path $publishDirectory '首次使用.md')
    Copy-Item -LiteralPath 'docs\首次使用.md' -Destination (Join-Path $outputDirectory '首次使用.md')
    $noticesDirectory = Join-Path $publishDirectory 'ThirdPartyNotices'
    $null = New-Item -ItemType Directory -Path $noticesDirectory
    foreach ($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','system.security.cryptography.protecteddata')) {
        $packageDirectory = Join-Path $environmentValues.NUGET_PACKAGES "$package\10.0.12"
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object Name -Match '^(?:LICENSE|THIRD-PARTY-NOTICES|THIRDPARTYNOTICES)')
        if ($notices.Count -eq 0) { throw "缺少随包许可：$package" }
        foreach ($notice in $notices) {
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $noticesDirectory "$package-$($notice.Name)")
        }
    }
    & $CompilerPath "/DAppVersion=$Version" "/DNumericVersion=$numericVersion" "/DPublishDir=$publishDirectory" `
        "/DOutputDir=$outputDirectory" 'installer\windows.iss' *> (Join-Path $buildRoot 'installer.log')
    if ($LASTEXITCODE -ne 0) { throw "安装器编译失败，见 $buildRoot/installer.log。" }
    $setupName = "SenseNova.TokenBurner-$Version-win-x64-setup.exe"
    $setup = Join-Path $outputDirectory $setupName
    if (-not (Test-Path -LiteralPath $setup)) { throw '安装器未生成预期产物。' }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS.txt'),"$hash  $setupName`n",[Text.UTF8Encoding]::new($false))
    $manifest = [ordered]@{
        Version=$Version; Runtime='10.0.12'; RuntimeIdentifier='win-x64'; SelfContained=$true
        SourceCommit=(& git rev-parse HEAD | Out-String).Trim()
        SourceHasUncommittedChanges=(@(& git status --porcelain).Count -gt 0)
        Setup=$setupName; SetupBytes=(Get-Item -LiteralPath $setup).Length; SHA256=$hash
        PublishBytes=(@(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File) | Measure-Object Length -Sum).Sum
        PublishFiles=@(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | ForEach-Object {
            [pscustomobject]@{Path=[IO.Path]::GetRelativePath($publishDirectory,$_.FullName);Bytes=$_.Length}
        })
        Signing='Unsigned application/setup; developer tool signature verified separately'
        InstallationAcceptance='Not performed by build script'; NetworkOrCredentialTest=$false
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $buildRoot 'manifest.json') -Encoding utf8
    $buildRoot | Set-Content -LiteralPath '.local\releases\latest-build-path.txt' -Encoding utf8
    Write-Output "安装包：$setup"
    Write-Output ("安装包 {0:N2} MiB，安装文件 {1:N2} MiB。" -f ($manifest.SetupBytes / 1MB),($manifest.PublishBytes / 1MB))
} finally {
    Pop-Location
    foreach ($name in $previous.Keys) {
        $value = if ($null -eq $previous[$name]) { [NullString]::Value } else { $previous[$name] }
        [Environment]::SetEnvironmentVariable($name,$value,'Process')
    }
}
