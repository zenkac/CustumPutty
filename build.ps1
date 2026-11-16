param([switch]$Portable, [string]$OutputDirectory = 'artifacts\portable-v1.8.3')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
if ($Portable) {
  & dotnet publish (Join-Path $taskRoot 'src\EquipLink.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $taskRoot $OutputDirectory) -p:MSBuildEnableWorkloadResolver=false --ignore-failed-sources
} else {
  & dotnet build (Join-Path $taskRoot 'src\EquipLink.csproj') -c Release --no-self-contained -p:MSBuildEnableWorkloadResolver=false --ignore-failed-sources
}
if ($LASTEXITCODE -ne 0) { throw 'EquipLink build failed.' }


