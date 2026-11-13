param(
    [string[]]$Suite=@('ftp-delete','ftp-explorer','ftp-tools','ftp-drag','ftp-ui'),
    [string]$Executable
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if(-not $Executable){$Executable=Join-Path $taskRoot 'artifacts\portable-v1.8.1\EquipLink.exe'}
$taskExe=(Resolve-Path -LiteralPath $Executable).Path
$taskAllowed=@('self','dialog','login','smoke','focus','theme','ftp','ftp-tools','ftp-ui','ftp-drag','ftp-explorer','ftp-delete','sftp')
$taskPreviousFixture=$env:EQUIPLINK_SFTP_FIXTURE
$env:EQUIPLINK_SFTP_FIXTURE=Join-Path $PSScriptRoot 'sftp-fixture.py'
$taskRun=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
try {
    foreach($taskSuite in $Suite) {
        if($taskSuite -notin $taskAllowed){throw ('Unknown test suite: '+$taskSuite)}
        $taskOutput=Join-Path $taskRoot ('artifacts\tests\'+$taskRun+'-'+$taskSuite)
        if(Test-Path -LiteralPath $taskOutput){throw 'Test directory already exists'}
        $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--'+$taskSuite+'-test',('"'+$taskOutput+'"')) -WindowStyle Hidden -Wait -PassThru
        if($taskProcess.ExitCode -ne 0) {
            $taskError=Join-Path $taskOutput 'startup-error.txt'
            if(Test-Path -LiteralPath $taskError){Get-Content -LiteralPath $taskError}
            throw ($taskSuite+' failed with exit '+$taskProcess.ExitCode)
        }
        Write-Output ($taskSuite+': passed; reports in '+$taskOutput)
    }
} finally {$env:EQUIPLINK_SFTP_FIXTURE=$taskPreviousFixture}
