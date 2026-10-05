#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\ResourceSampling.ps1')
$cases = @(
    @{ Current=1.125; Previous=1.0; Interval=0.5; Processors=20; ExpectedDelta=0.125; ExpectedPercent=1.25 }
    @{ Current=0.015625; Previous=0.0; Interval=1.0; Processors=20; ExpectedDelta=0.015625; ExpectedPercent=0.078125 }
    @{ Current=3.0; Previous=3.0; Interval=2.0; Processors=4; ExpectedDelta=0.0; ExpectedPercent=0.0 }
    @{ Current=1.0; Previous=2.0; Interval=1.0; Processors=4; ExpectedDelta=0.0; ExpectedPercent=0.0 }
)
foreach ($case in $cases) {
    $actual = Get-NormalizedCpuInterval -CurrentCpuSeconds $case.Current -PreviousCpuSeconds $case.Previous -IntervalSeconds $case.Interval -LogicalProcessorCount $case.Processors
    if ([Math]::Abs($actual.CpuSecondsDelta - $case.ExpectedDelta) -gt 1e-12 -or [Math]::Abs($actual.CpuPercent - $case.ExpectedPercent) -gt 1e-12) {
        throw "CPU 精度回归失败：预期差值 $($case.ExpectedDelta)、占比 $($case.ExpectedPercent)，实际差值 $($actual.CpuSecondsDelta)、占比 $($actual.CpuPercent)。"
    }
}
foreach ($invalid in @(@{Interval=0; Processors=20}, @{Interval=-1; Processors=20}, @{Interval=1; Processors=0})) {
    $rejected = $false
    try { $null = Get-NormalizedCpuInterval -CurrentCpuSeconds 1 -PreviousCpuSeconds 0 -IntervalSeconds $invalid.Interval -LogicalProcessorCount $invalid.Processors }
    catch { $rejected = $true }
    if (-not $rejected) { throw '非法 CPU 采样条件未被拒绝。' }
}
[PSCustomObject]@{ Result='PASS'; PrecisionCases=$cases.Count; InvalidCases=3 }
