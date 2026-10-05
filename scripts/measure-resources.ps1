#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Short', 'Engine', 'Panel')] [string]$Baseline = 'Short',
    [ValidateRange(1, 3600)] [int]$IdleSeconds = 120,
    [ValidateRange(1, 1800)] [int]$LoadSeconds = 60,
    [ValidateRange(0, 60)] [int]$WarmupSeconds = 10
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\ResourceSampling.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$executableRelativePath = 'src\SenseNova.TokenBurner.Desktop\bin\Release\net10.0-windows\SenseNova.TokenBurner.exe'
$processName = 'SenseNova.TokenBurner'
$expectedWindowTitle = 'SenseNova Token Burner · 模拟面板'
$executable = Join-Path $projectRoot $executableRelativePath
if (-not (Test-Path -LiteralPath $executable)) {
    throw '请先运行 dev.ps1 build，准备 Release 程序。'
}
$existing = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
if ($existing.Count) { throw '同路径应用已在运行，请正常退出后再测量。脚本不会结束已有进程。' }
$localSdk = Join-Path $projectRoot '.local\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localSdk) { $runtimeRoot = Split-Path -Parent $localSdk }
else {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { throw '未找到开发运行时，请按 README 准备固定 SDK。' }
    $runtimeRoot = Split-Path -Parent $dotnetCommand.Source
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$reportDirectory = Join-Path $projectRoot ('.local\verification\resources\' + $runId)
$null = New-Item -ItemType Directory -Path $reportDirectory
$statePath = Join-Path $reportDirectory 'probe-state.json'
$startInfo = [Diagnostics.ProcessStartInfo]::new($executable)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WorkingDirectory = $projectRoot
$probeFlag = if ($Baseline -eq 'Engine') { '--engine-resource-baseline' } elseif ($Baseline -eq 'Panel') { '--panel-resource-baseline' } else { '--resource-baseline' }
foreach ($argument in @($probeFlag, $reportDirectory, "$IdleSeconds", "$LoadSeconds", "$WarmupSeconds")) {
    $startInfo.ArgumentList.Add($argument)
}
# 只配置子进程，不改变调用者环境或持久设置。
$startInfo.Environment['DOTNET_ROOT'] = $runtimeRoot
$startInfo.Environment['SENSENOVA_MOCK_PROFILE'] = Join-Path $reportDirectory 'profile'
$samples = [System.Collections.Generic.List[object]]::new()
$application = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $application) { throw '资源测量应用未启动。' }
$clock = [Diagnostics.Stopwatch]::StartNew()
$previousSeconds = 0.0
$previousCpuSeconds = 0.0
$previousPhase = 'Startup'
$nextSampleSeconds = 1.0
$nextProgressSeconds = 30.0
$reportedPhase = ''
$observedProcessIds = [System.Collections.Generic.HashSet[int]]::new()
$graphicsDriverModules = @()
$wpfModules = @()
$windowTitleObserved = $false
$null = $observedProcessIds.Add($application.Id)
try {
    Write-Output "测量启动：$Baseline，预热 $WarmupSeconds 秒，等待 $IdleSeconds 秒，负载时限 $LoadSeconds 秒。"
    $loadBudget = if ($Baseline -eq 'Engine') { 2 * $LoadSeconds } else { $LoadSeconds }
    while (-not $application.HasExited) {
        if ($clock.Elapsed.TotalSeconds -gt ($WarmupSeconds + $IdleSeconds + $loadBudget + 30)) {
            throw '测量未按时完成，保留现场资料；脚本不会强制结束进程。'
        }
        $remainingMilliseconds = [Math]::Max(1, [int](($nextSampleSeconds - $clock.Elapsed.TotalSeconds) * 1000))
        Start-Sleep -Milliseconds $remainingMilliseconds
        $elapsed = $clock.Elapsed.TotalSeconds
        $nextSampleSeconds = $elapsed + 1
        $application.Refresh()
        if ($application.HasExited) { break }
        $phase = 'Startup'
        if (Test-Path -LiteralPath $statePath) { $phase = (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Phase }
        if ($phase -ne $reportedPhase) {
            Write-Output ("场景 {0}，总计 {1:N1} 秒。" -f $phase, $elapsed)
            $reportedPhase = $phase
            if ($phase -eq 'Idle') {
                $graphicsDriverModules = @($application.Modules | Where-Object { $_.ModuleName -match '^(nv|igd|ati)' } | ForEach-Object ModuleName)
                $wpfModules = @($application.Modules | Where-Object { $_.ModuleName -match '^(Presentation|wpfgfx)' } | ForEach-Object ModuleName)
                $windowTitleObserved = $application.MainWindowTitle -eq $expectedWindowTitle
            }
        }
        # 统计同一构建路径的全部应用进程，排除 PowerShell、SDK 和其他程序。
        $processes = @(Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
        $application.Refresh()
        if ($processes.Count -eq 0 -and $application.HasExited) { break }
        if ($processes.Count -ne 1 -or $processes[0].Id -ne $application.Id) {
            throw '测量期间应用进程集合发生变化，本轮不能作为单进程基线。'
        }
        $currentCpuSeconds = 0.0
        $workingSetBytes = 0L
        $privateBytes = 0L
        $processPeakBytes = 0L
        foreach ($process in $processes) {
            $null = $observedProcessIds.Add($process.Id)
            $currentCpuSeconds += $process.TotalProcessorTime.TotalSeconds
            $workingSetBytes += $process.WorkingSet64
            $privateBytes += $process.PrivateMemorySize64
            $processPeakBytes += $process.PeakWorkingSet64
        }
        $intervalSeconds = $elapsed - $previousSeconds
        $cpuInterval = Get-NormalizedCpuInterval -CurrentCpuSeconds $currentCpuSeconds -PreviousCpuSeconds $previousCpuSeconds -IntervalSeconds $intervalSeconds -LogicalProcessorCount ([Environment]::ProcessorCount)
        $samples.Add([PSCustomObject]@{
            ElapsedSeconds=$elapsed; Phase=$phase; ProcessCount=$processes.Count
            IntervalSeconds=$intervalSeconds; CpuSecondsDelta=$cpuInterval.CpuSecondsDelta
            CpuPercent=$cpuInterval.CpuPercent
            WorkingSetMiB=$workingSetBytes / 1MB; PrivateMiB=$privateBytes / 1MB
            ProcessLifetimePeakWorkingSetMiB=$processPeakBytes / 1MB
            IncludeCpu=($phase -eq $previousPhase -and $phase -in @('Idle','Load','LoadPlain','LoadPersisted'))
        })
        $previousSeconds=$elapsed; $previousCpuSeconds=$currentCpuSeconds; $previousPhase=$phase
        if ($elapsed -ge $nextProgressSeconds) {
            Write-Output ("已采样 {0:N0} 秒，工作集 {1:N1} MiB。" -f $elapsed, ($workingSetBytes / 1MB))
            $nextProgressSeconds=$elapsed+30
        }
    }
    $application.WaitForExit()
    if (-not (Test-Path -LiteralPath $statePath)) { throw '应用未生成测量状态，当前基线未验证。' }
    $finalState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($finalState.Frontend -ne 'WinForms') { throw '程序不是当前 WinForms 测量版本，请重新构建。' }
    if ($application.ExitCode -ne 0 -or $finalState.Phase -ne 'Completed') { throw '应用测量提前结束，当前基线未验证。' }
    $scenarioNames = if ($Baseline -eq 'Engine') { @('LoadPlain','LoadPersisted','Idle') } else { @('Idle','Load') }
    $scenarios = foreach ($name in $scenarioNames) {
        $rows = @($samples | Where-Object { $_.Phase -eq $name })
        $cpuRows = @($rows | Where-Object IncludeCpu)
        if ($rows.Count -lt 2 -or $cpuRows.Count -eq 0) { throw "场景 $name 采样不足，请增加时长。" }
        $sampledCpuSeconds = ($cpuRows | Measure-Object CpuSecondsDelta -Sum).Sum
        $sampledSeconds = ($cpuRows | Measure-Object IntervalSeconds -Sum).Sum
        $averageCpu = 100 * $sampledCpuSeconds / $sampledSeconds / [Environment]::ProcessorCount
        $peakWorkingSet = ($rows | Measure-Object WorkingSetMiB -Maximum).Maximum
        $targetWorkingSet = if ($name -eq 'Idle') { 100 } else { 150 }
        [PSCustomObject]@{
            Name=$name; Samples=$rows.Count; CpuCoveredSeconds=$sampledSeconds
            AverageCpuPercent=$averageCpu
            MaximumSampleCpuPercent=($cpuRows | Measure-Object CpuPercent -Maximum).Maximum
            AverageWorkingSetMiB=($rows | Measure-Object WorkingSetMiB -Average).Average
            MaximumSampleWorkingSetMiB=$peakWorkingSet
            MaximumPrivateMiB=($rows | Measure-Object PrivateMiB -Maximum).Maximum
            TargetWorkingSetMiB=$targetWorkingSet; WorkingSetTargetMet=($peakWorkingSet -le $targetWorkingSet)
            IdleCpuTargetMet=$(if ($name -eq 'Idle') { $averageCpu -le 1 } else { $null })
        }
    }
    $report = [PSCustomObject]@{
        SchemaVersion=2; TimestampUtc=[DateTime]::UtcNow.ToString('o'); Build='Release'
        CpuMeasurement='Fractional process CPU seconds, clamped as double; whole-machine percent'
        BaselineKind=$(if ($Baseline -eq 'Engine') { 'Window with full concurrent HTTP mock, persistent history and five-hour schedule wait' } elseif ($Baseline -eq 'Panel') { 'Manual panel round with generated input, persistent VM and post-load idle' } else { 'Window with short serial in-process HTTP mock' }); Frontend='WinForms'
        OperatingSystem=[Environment]::OSVersion.VersionString; RuntimeVersion=$finalState.RuntimeVersion
        LogicalProcessors=[Environment]::ProcessorCount; SampleIntervalSeconds=1
        WarmupSeconds=$WarmupSeconds; IdleSeconds=$IdleSeconds; LoadSeconds=$LoadSeconds
        ProcessCount=$observedProcessIds.Count; ApplicationExitCode=$application.ExitCode
        SuccessfulMockRequests=$finalState.SuccessfulMockRequests; SimulatedTokens=$finalState.SimulatedTokens
        OverallProcessPeakWorkingSetMiB=($samples | Measure-Object ProcessLifetimePeakWorkingSetMiB -Maximum).Maximum
        NetworkEnabled=$false; Scenarios=@($scenarios)
        GraphicsDriverModules=$graphicsDriverModules
        WindowTitleObserved=$windowTitleObserved
        LoadedWpfModules=$wpfModules
        Engine=$(if ($Baseline -eq 'Engine') { $finalState.Engine } else { $null })
        Panel=$(if ($Baseline -eq 'Panel') { $finalState.Panel } else { $null })
        Limitations=@($(if ($Baseline -eq 'Engine') { 'Synthetic input and virtual usage; no real API, tray or installed release verification. Plain round runs before persisted round; caches and GC prevent a strict causal disk-cost comparison.' } elseif ($Baseline -eq 'Panel') { 'Panel VM round and generated text with virtual usage; no real API, tray, interactive UI or installed release verification. Plan remains disabled.' } else { 'Short mock requests only; no full target, long input, tray or real API verification.' }), 'Phase transition CPU samples excluded; one-second sampling can miss brief memory peaks. Process IO write counters are logical IO and do not measure physical disk or power-loss durability.')
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory 'summary.json') -Encoding utf8
    $scenarios | Format-Table Name,Samples,AverageCpuPercent,MaximumSampleWorkingSetMiB,WorkingSetTargetMet,IdleCpuTargetMet -AutoSize | Out-String | Write-Output
    Write-Output "结果目录：$reportDirectory"
} finally {
    $samples | Export-Csv -LiteralPath (Join-Path $reportDirectory 'samples.csv') -NoTypeInformation -Encoding utf8
    $application.Dispose()
}
