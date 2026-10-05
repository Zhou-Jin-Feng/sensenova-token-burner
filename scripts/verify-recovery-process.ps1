[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $repo ('.local/verification/recovery-process-' + [Guid]::NewGuid().ToString('N'))
$profile = Join-Path $probe 'profile'
$sdk = Join-Path $repo '.local/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
New-Item -ItemType Directory -Path $probe | Out-Null
$core = [System.Security.SecurityElement]::Escape((Join-Path $repo 'src/SenseNova.TokenBurner.Core/bin/Release/net10.0/SenseNova.TokenBurner.Core.dll'))
$infrastructure = [System.Security.SecurityElement]::Escape((Join-Path $repo 'src/SenseNova.TokenBurner.Infrastructure/bin/Release/net10.0-windows/SenseNova.TokenBurner.Infrastructure.dll'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Reference Include="SenseNova.TokenBurner.Core"><HintPath>$core</HintPath></Reference>
    <Reference Include="SenseNova.TokenBurner.Infrastructure"><HintPath>$infrastructure</HintPath></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $probe 'RecoveryProbe.csproj') -Encoding utf8
@'
using System.Text.Json;
using SenseNova.TokenBurner.Core;
using SenseNova.TokenBurner.Infrastructure.Storage;

using var instance = UserInstanceLock.TryAcquire(args[1]) ?? throw new Exception("Instance lock failed");
var executor = new ProbeExecutor(args[0] == "crash");
var runtime = new PersistentRunSession(executor, new JsonRunStateStore(args[1]));
await runtime.InitializeAsync();
var parameters = new RunParameters("mock-model", 100) { RequestTokenReservation = 100 };
if (args[0] == "crash")
{
    await runtime.RunOnceAsync(parameters).WaitAsync(TimeSpan.FromSeconds(10));
    return 3;
}
var unresolved = runtime.Recovery.UnresolvedRequests;
if (runtime.Recovery.State != RecoveryState.NeedsReview || unresolved != 1
    || executor.Started != 0 || runtime.Schedule.State != ScheduleState.Disabled) return 4;
try { await runtime.RunOnceAsync(parameters); return 5; } catch (InvalidOperationException) { }
await runtime.ConfirmRecoveryAsync(true);
if (executor.Started != 0 || runtime.Recovery.UnresolvedRequests != 1) return 6;
await runtime.RunOnceAsync(parameters).WaitAsync(TimeSpan.FromSeconds(10));
if (executor.Started != 1 || runtime.Recovery.Document!.History.Single().Snapshot.InFlightRequests != 1) return 7;
Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", startupRequests = 0,
    unresolvedAfterExit = unresolved, requestsAfterExplicitStart = executor.Started,
    retainedInterruptedHistory = true, instanceLockReacquired = true }));
return 0;

sealed class ProbeExecutor(bool crash) : IRunRequestExecutor
{
    public int Started { get; private set; }
    public Task<TokenUsage> ExecuteAsync(RunParameters parameters, CancellationToken cancellationToken)
    {
        Started++;
        if (crash) Environment.Exit(23); // 仅此隔离 helper 直接退出；不结束外部进程。
        return Task.FromResult(new TokenUsage(90, 10, 100));
    }
}
'@ | Set-Content -LiteralPath (Join-Path $probe 'Program.cs') -Encoding utf8
$variables = @{
    DOTNET_ROOT = (Split-Path -Parent $sdk)
    DOTNET_CLI_HOME = (Join-Path $repo '.local/cli-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    NUGET_PACKAGES = (Join-Path $repo '.local/nuget/packages')
}
$previous = @{}
try {
    foreach ($name in $variables.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    & $sdk build (Join-Path $probe 'RecoveryProbe.csproj') --configuration Release *> (Join-Path $probe 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "恢复验证 helper 构建失败，详见 $probe/build.log" }
    $dll = Join-Path $probe 'bin/Release/net10.0-windows/RecoveryProbe.dll'
    foreach ($mode in @('crash', 'reopen')) {
        $start = [System.Diagnostics.ProcessStartInfo]::new($sdk)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @($dll, $mode, $profile)) { $start.ArgumentList.Add($argument) }
        $process = [System.Diagnostics.Process]::Start($start)
        try {
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(15000)) { throw '隔离验证进程未按时退出；停止后续验证，未强制结束进程。' }
            $expected = if ($mode -eq 'crash') { 23 } else { 0 }
            $output.Result | Set-Content -LiteralPath (Join-Path $probe "$mode-output.log") -Encoding utf8
            $errors.Result | Set-Content -LiteralPath (Join-Path $probe "$mode-error.log") -Encoding utf8
            if ($process.ExitCode -ne $expected) { throw "$mode 验证失败，退出码 $($process.ExitCode)，详见隔离日志。" }
            if ($mode -eq 'reopen') {
                $report = $output.Result | ConvertFrom-Json
                if ($report.status -ne 'PASS') { throw '恢复行为报告未通过' }
                $output.Result | Set-Content -LiteralPath (Join-Path $repo '.local/verification/recovery-process-report.json') -Encoding utf8
                $output.Result
            }
        } finally { $process.Dispose() }
    }
} finally {
    foreach ($name in $previous.Keys) {
        $value = if ($null -eq $previous[$name]) { [NullString]::Value } else { $previous[$name] }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}
