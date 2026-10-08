#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$ToolchainLock,
    [Parameter(Mandatory)][string]$SdkRoot,
    [Parameter(Mandatory)][string]$EnvironmentDirectory,
    [Parameter(Mandatory)][string]$RunDirectory,
    [ValidateRange(5580,5584)][int]$Port = 5584
)
$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath $ToolchainLock -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -cne $lock.powershell) { throw 'PowerShell differs from root lock' }
$profile = $lock.flutterAndroidProbe.api37Windows
$evidence = Get-Content -LiteralPath (Join-Path $EnvironmentDirectory 'environment-evidence.json') -Raw | ConvertFrom-Json
if (!$profile -or $evidence.kind -cne 'isolated-api37-environment' -or
    ($profile | ConvertTo-Json -Depth 12 -Compress) -cne ($evidence.profile | ConvertTo-Json -Depth 12 -Compress) -or
    $profile.memoryMb -ne 4096 -or $profile.cores -ne 2) { throw 'Environment differs from the root API37 profile' }
foreach ($name in @('emulator','image')) {
    $archive = Join-Path $EnvironmentDirectory "$name.zip"
    if ((Get-Item -LiteralPath $archive).Length -ne $profile.$name.size -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash.ToLowerInvariant() -cne $profile.$name.sha1) { throw 'Retained archive differs from the root lock' }
}
$exe = Join-Path $EnvironmentDirectory 'emulator/emulator/emulator.exe'
$image = Join-Path $EnvironmentDirectory 'image/x86_64'
$emulatorProperties = Get-Content -LiteralPath (Join-Path $EnvironmentDirectory 'emulator/emulator/source.properties') -Raw
$imageProperties = Get-Content -LiteralPath (Join-Path $image 'source.properties') -Raw
if ($emulatorProperties -cne $evidence.emulatorProperties -or $imageProperties -cne $evidence.imageProperties) { throw 'Extracted properties changed after installation validation' }
if ($Port % 2 -ne 0 -or (Test-Path -LiteralPath $RunDirectory) -or ![IO.Path]::IsPathFullyQualified($RunDirectory)) { throw 'A new absolute AVD run directory and even port are required' }
$adb = Join-Path $SdkRoot 'platform-tools/adb.exe'
if (!(Test-Path -LiteralPath $adb) -or !(Test-Path -LiteralPath $exe)) { throw 'Required explicit tool is absent' }
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -In @($Port,($Port+1)))
if ($listeners.Count) { throw 'Private emulator port is already in use' }
$avdHome = Join-Path $RunDirectory 'avd'
$avd = Join-Path $avdHome 'mystia_api37_p0.avd'
New-Item -ItemType Directory -Path $avd | Out-Null
$config = @"
AvdId=mystia_api37_p0
avd.ini.displayname=Mystia isolated API37 target36 P0
avd.ini.encoding=UTF-8
abi.type=x86_64
hw.cpu.arch=x86_64
hw.cpu.ncore=$($profile.cores)
hw.ramSize=$($profile.memoryMb)
hw.lcd.width=480
hw.lcd.height=800
hw.lcd.density=160
hw.gpu.enabled=yes
hw.gpu.mode=host
hw.keyboard=yes
hw.audioInput=no
hw.audioOutput=no
hw.sdCard=no
disk.dataPartition.size=4G
image.sysdir.1=$($image.Replace('\','/'))/
showDeviceFrame=no
fastboot.forceColdBoot=yes
fastboot.forceFastBoot=no
PlayStore.enabled=false
"@
[IO.File]::WriteAllText((Join-Path $avd 'config.ini'), $config)
[IO.File]::WriteAllText((Join-Path $avdHome 'mystia_api37_p0.ini'), "avd.ini.encoding=UTF-8`npath=$avd`ntarget=android-$($profile.image.api)`n")
$env:ANDROID_AVD_HOME = $avdHome
$env:ANDROID_SDK_ROOT = $SdkRoot
$env:ANDROID_HOME = $SdkRoot
$process = Start-Process -FilePath $exe -ArgumentList @('-avd','mystia_api37_p0','-no-window','-no-audio','-no-snapshot','-no-boot-anim','-gpu','host','-memory',"$($profile.memoryMb)",'-cores',"$($profile.cores)",'-port',"$Port") -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'emulator.stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'emulator.stderr.log')
[ordered]@{ schemaVersion=1; pid=$process.Id; executable=$exe; executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant(); driverSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant(); avdDirectory=$avd; serial="emulator-$Port"; profile=$profile; rootLockSha256=(Get-FileHash -LiteralPath $ToolchainLock -Algorithm SHA256).Hash.ToLowerInvariant(); imageProperties=$imageProperties; gpuMode='host'; startedAt=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $RunDirectory 'emulator-start.json') -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $RunDirectory 'emulator-start.json')
& $adb start-server
while (!$process.HasExited -and !(Test-Path -LiteralPath (Join-Path $RunDirectory 'stop-requested'))) {
    & $adb start-server 2>&1 | Out-Null
    Start-Sleep -Seconds 10
    $process.Refresh()
}
$stopRequested = !$process.HasExited
if ($stopRequested) {
    & $adb -s "emulator-$Port" emu kill
    if ($LASTEXITCODE -ne 0) { throw 'Normal emulator stop command failed; retain the owned process identity for cleanup' }
    if (!$process.WaitForExit(30000)) { throw 'Owned emulator has not exited after its normal stop request; retain logs and process identity' }
}
$process.WaitForExit()
[ordered]@{ schemaVersion=1; pid=$process.Id; serial="emulator-$Port"; stopRequested=$stopRequested; exited=$process.HasExited; exitCode=$process.ExitCode; observedAt=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDirectory 'emulator-exit.json') -Encoding utf8NoBOM
if ($process.ExitCode -ne 0) { throw "Isolated API37 emulator exited with code $($process.ExitCode); retain logs" }
