[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('info', 'restore', 'build', 'test', 'run')]
    [string]$Action = 'info',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$UpdateLockFiles
)

$ErrorActionPreference = 'Stop'
if ($UpdateLockFiles -and $Action -ne 'restore') { throw 'UpdateLockFiles 仅用于有意更新依赖锁文件。' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.local\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetExecutable = $localDotnet
} else {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '未找到 .NET SDK。请按 README 准备 global.json 指定的版本。'
    }
    $dotnetExecutable = $dotnetCommand.Source
}

$environmentValues = @{
    DOTNET_ROOT = (Split-Path -Parent $dotnetExecutable)
    DOTNET_CLI_HOME = (Join-Path $projectRoot '.local\cli-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    NUGET_PACKAGES = (Join-Path $projectRoot '.local\nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $projectRoot '.local\nuget\http-cache')
    SENSENOVA_MOCK_PROFILE = (Join-Path $projectRoot '.local\mock-profile')
}
$previousValues = @{}
foreach ($name in $environmentValues.Keys) {
    $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environmentValues[$name], 'Process')
}

Push-Location -LiteralPath $projectRoot
try {
    $sdkVersion = (& $dotnetExecutable --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'SDK 版本检查失败。请准备 global.json 指定的 SDK。'
    }
    $requiredVersion = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    if ($sdkVersion -ne $requiredVersion) {
        throw "需要 SDK $requiredVersion，实际为 $sdkVersion。"
    }

    switch ($Action) {
        'info' { & $dotnetExecutable --info }
        'restore' {
            if ($UpdateLockFiles) { & $dotnetExecutable restore 'SenseNova.TokenBurner.slnx' --force-evaluate }
            else { & $dotnetExecutable restore 'SenseNova.TokenBurner.slnx' --locked-mode }
        }
        'build' { & $dotnetExecutable build 'SenseNova.TokenBurner.slnx' --no-restore --configuration $Configuration }
        'test' { & $dotnetExecutable test --solution 'SenseNova.TokenBurner.slnx' --no-build --configuration $Configuration }
        'run' { & $dotnetExecutable run --project 'src/SenseNova.TokenBurner.Desktop' --no-build --no-restore --configuration $Configuration }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $Action 失败，退出码 $LASTEXITCODE。"
    }
} finally {
    Pop-Location
    foreach ($name in $previousValues.Keys) {
        if ($null -eq $previousValues[$name]) {
            # PowerShell 将普通 $null 绑定为 string.Empty；NullString 才能删除原本不存在的变量。
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($name, $previousValues[$name], 'Process')
        }
    }
}
