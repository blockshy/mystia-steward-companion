#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$SdkRoot,
    [Parameter(Mandatory)][string]$ImageDirectory,
    [Parameter(Mandatory)][string]$RunDirectory,
    [ValidateSet('x86_64','armeabi-v7a','x86')][string]$Abi = 'x86_64',
    [ValidateSet('swiftshader_indirect','host')][string]$GpuMode = 'swiftshader_indirect',
    [ValidateRange(5580,5584)][int]$Port = 5580
)
$ErrorActionPreference = 'Stop'
if ($Port % 2 -ne 0) { throw 'Emulator console port must be even' }
if (Test-Path $RunDirectory) { throw 'Run directory already exists' }
$properties = Get-Content (Join-Path $ImageDirectory 'source.properties') -Raw
if ($properties -notmatch "SystemImage.Abi=$([regex]::Escape($Abi))") { throw 'Wrong image ABI' }
if ($properties -notmatch 'AndroidVersion.ApiLevel=([0-9]+)') { throw 'Missing image API level' }
$apiLevel = $Matches[1]
$avdHome = Join-Path $RunDirectory 'avd'
$avd = Join-Path $avdHome 'mystia_p0.avd'
New-Item -ItemType Directory -Path $avd | Out-Null
$cpu = if ($Abi -eq 'armeabi-v7a') { 'arm' } else { $Abi }
$config = @"
AvdId=mystia_p0
avd.ini.displayname=Mystia isolated P0
avd.ini.encoding=UTF-8
abi.type=$Abi
hw.cpu.arch=$cpu
hw.cpu.ncore=2
hw.ramSize=1536
hw.lcd.width=480
hw.lcd.height=800
hw.lcd.density=160
hw.gpu.enabled=yes
hw.gpu.mode=$GpuMode
hw.keyboard=yes
hw.audioInput=no
hw.audioOutput=no
hw.sdCard=no
disk.dataPartition.size=2G
image.sysdir.1=$($ImageDirectory.Replace('\','/'))/
showDeviceFrame=no
fastboot.forceColdBoot=yes
fastboot.forceFastBoot=no
PlayStore.enabled=false
"@
[IO.File]::WriteAllText((Join-Path $avd 'config.ini'), $config)
[IO.File]::WriteAllText((Join-Path $avdHome 'mystia_p0.ini'), "avd.ini.encoding=UTF-8`npath=$avd`ntarget=android-$apiLevel`n")
$env:ANDROID_AVD_HOME = $avdHome
$env:ANDROID_SDK_ROOT = $SdkRoot
$env:ANDROID_HOME = $SdkRoot
$exe = Join-Path $SdkRoot 'emulator/emulator.exe'
$process = Start-Process -FilePath $exe -ArgumentList @('-avd','mystia_p0','-no-window','-no-audio','-no-snapshot','-no-boot-anim','-gpu',$GpuMode,'-memory','1536','-cores','2','-port',"$Port") -PassThru -RedirectStandardOutput (Join-Path $RunDirectory 'emulator.stdout.log') -RedirectStandardError (Join-Path $RunDirectory 'emulator.stderr.log')
[ordered]@{ schemaVersion=1; pid=$process.Id; executable=$exe; avdDirectory=$avd; serial="emulator-$Port"; imageDirectory=$ImageDirectory; imageProperties=$properties; gpuMode=$GpuMode; startedAt=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $RunDirectory 'emulator-start.json') -Encoding utf8NoBOM
Get-Content (Join-Path $RunDirectory 'emulator-start.json')
$adb = Join-Path $SdkRoot 'platform-tools/adb.exe'
& $adb start-server
while (-not $process.HasExited -and -not (Test-Path (Join-Path $RunDirectory 'stop-requested'))) {
    # The previous SSH keeper may own an adb server which exits shortly after
    # this AVD starts. Recreate it under this live keeper if needed; never kill it.
    & $adb start-server 2>&1 | Out-Null
    Start-Sleep -Seconds 10
    $process.Refresh()
}
if (-not $process.HasExited) { & $adb -s "emulator-$Port" emu kill }
else {
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Isolated emulator exited with code $($process.ExitCode); inspect its stdout/stderr evidence" }
}
