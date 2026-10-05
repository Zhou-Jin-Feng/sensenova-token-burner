function Get-NormalizedCpuInterval {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [double]$CurrentCpuSeconds,
        [Parameter(Mandatory)] [double]$PreviousCpuSeconds,
        [Parameter(Mandatory)] [double]$IntervalSeconds,
        [Parameter(Mandatory)] [int]$LogicalProcessorCount
    )
    if ($IntervalSeconds -le 0 -or $LogicalProcessorCount -lt 1) { throw 'CPU 采样间隔和处理器数必须为正数。' }
    # PowerShell 会按首参数 0 选择 int 重载，必须显式使用 double 以保留小数 CPU 秒。
    $cpuDelta = [Math]::Max([double]0, $CurrentCpuSeconds - $PreviousCpuSeconds)
    [PSCustomObject]@{
        CpuSecondsDelta = $cpuDelta
        CpuPercent = 100 * $cpuDelta / $IntervalSeconds / $LogicalProcessorCount
    }
}
