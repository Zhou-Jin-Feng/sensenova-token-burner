[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$variableNames = @(
    'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO',
    'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH',
    'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'SENSENOVA_MOCK_PROFILE'
)
$originalValues = @{}
foreach ($variableName in $variableNames) {
    $originalValues[$variableName] = [Environment]::GetEnvironmentVariable($variableName, 'Process')
}
$initialDirectory = (Get-Location).Path

try {
    foreach ($scenario in @('absent', 'present', 'empty')) {
        $expectedValues = @{}
        foreach ($variableName in $variableNames) {
            switch ($scenario) {
                'absent' { [Environment]::SetEnvironmentVariable($variableName, [NullString]::Value, 'Process') }
                'present' { [Environment]::SetEnvironmentVariable($variableName, 'sensenova-verification-sentinel', 'Process') }
                'empty' { [Environment]::SetEnvironmentVariable($variableName, '', 'Process') }
            }
            $expectedValues[$variableName] = [Environment]::GetEnvironmentVariable($variableName, 'Process')
        }

        & (Join-Path $PSScriptRoot 'dev.ps1') info | Out-Null

        foreach ($variableName in $variableNames) {
            $actualValue = [Environment]::GetEnvironmentVariable($variableName, 'Process')
            if ($actualValue -cne $expectedValues[$variableName]) {
                throw "Environment restoration failed: $scenario / $variableName"
            }
        }
        if ((Get-Location).Path -ne $initialDirectory) {
            throw "Working directory restoration failed: $scenario"
        }
    }

    [pscustomobject]@{Result='PASS';Scenarios=3;EnvironmentVariables=$variableNames.Count;WorkingDirectoryRestored=$true}
} finally {
    foreach ($variableName in $variableNames) {
        if ($null -eq $originalValues[$variableName]) {
            [Environment]::SetEnvironmentVariable($variableName, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($variableName, $originalValues[$variableName], 'Process')
        }
    }
}
