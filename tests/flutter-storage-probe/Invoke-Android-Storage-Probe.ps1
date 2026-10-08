#requires -Version 7.6
param(
 [Parameter(Mandatory)][string]$Adb,
 [Parameter(Mandatory)][string]$Serial,
 [Parameter(Mandatory)][string]$Bundle,
 [Parameter(Mandatory)][string]$RunId,
 [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$id='com.tyukki.mystia.steward.companion.storagep0'
if($RunId -cnotmatch '^[a-z0-9][a-z0-9-]{7,63}$' -or (Test-Path -LiteralPath $OutputDirectory)){throw 'New isolated run/output required'}
$build=Get-Content -LiteralPath (Join-Path $Bundle 'build-evidence.json') -Raw|ConvertFrom-Json
$apk=Join-Path $Bundle 'mystia-storage-p0-arm64.apk'
$record=@($build.files|Where-Object path -CEQ 'mystia-storage-p0-arm64.apk')
if($record.Count -ne 1 -or $build.kind -cne 'flutter-storage-p0-bundle' -or (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record[0].sha256){throw 'Wrong storage build'}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
function Adb([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments){
 $text=@(& $Adb -s $Serial @Arguments 2>&1);$code=$LASTEXITCODE
 [IO.File]::AppendAllText((Join-Path $OutputDirectory 'adb.log'),"adb $($Arguments -join ' ')`n$($text -join "`n")`nexit=$code`n")
 if($code -ne 0){throw 'ADB operation failed'}
 return ($text -join "`n").Trim()
}
Adb @('start-server')|Out-Null
if(Adb @('shell','pm','list','packages',$id)){throw 'Existing isolated package will not be replaced'}
if((Adb @('install','--no-streaming',$apk)) -notmatch 'Success'){throw 'Isolated installation failed'}
$installed=Adb @('shell','pm','path',$id)
if($installed -cnotmatch '^package:(/data/app/[A-Za-z0-9_+./=~-]+/base\.apk)$'){throw 'Unexpected installed path'}
if(!(Adb @('shell','sha256sum',$Matches[1])).StartsWith($record[0].sha256+' ')){throw 'Installed APK differs'}
$facts=[ordered]@{schemaVersion=1;runId=$RunId;gitSha=$build.gitSha;sourceDigest=$build.sourceDigest;apkSha256=$record[0].sha256;sdk=(Adb @('shell','getprop','ro.build.version.sdk'));abi=(Adb @('shell','getprop','ro.product.cpu.abilist'));pageSize=(Adb @('shell','getconf','PAGE_SIZE'));reports=@();status='FAIL';syntheticOnly=$true}
try {
 $pids=@()
 foreach($phase in @('write','read','delete','confirm')){
  Adb @('shell','am','force-stop',$id)|Out-Null
  Adb @('shell','am','start','-n',"$id/.MainActivity",'--es','run-id',$RunId,'--es','phase',$phase)|Out-Null
  $remote="/sdcard/Android/data/$id/files/storage-p0/$RunId/$phase.json"
  $deadline=[DateTimeOffset]::UtcNow.AddSeconds(90);$found=$false
  while([DateTimeOffset]::UtcNow -lt $deadline){
   & $Adb -s $Serial shell test -f $remote 2>$null
   if($LASTEXITCODE -eq 0){$found=$true;break}
   Start-Sleep -Milliseconds 500
  }
  if(!$found){throw "No report for $phase"}
  $local=Join-Path $OutputDirectory "$phase.json"
  Adb @('pull',$remote,$local)|Out-Null
  $value=Get-Content -LiteralPath $local -Raw|ConvertFrom-Json
  if($value.status -cne 'PASS' -or $value.phase -cne $phase -or $value.runId -cne $RunId -or $value.sourceDigest -cne $build.sourceDigest -or $value.gitSha -cne $build.gitSha -or $pids -contains $value.pid -or $value.native.debuggable -or $value.normalActivityStopBeforeEvidence -ne $true){throw 'Runtime phase/source/identity check failed'}
  $pids+=@($value.pid)
  $facts.reports+=@([ordered]@{phase=$phase;pid=$value.pid;checks=$value.checks.Count;sha256=(Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLowerInvariant()})
 }
 $facts.status='PASS'
} finally {
 Adb @('shell','am','force-stop',$id)|Out-Null
 $facts|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $OutputDirectory 'runtime-evidence.json') -Encoding utf8NoBOM
}
# Keep the isolated package/evidence until external verification. Uninstall only
# this package after comparing its installed APK hash again; never deleteAll.
$facts|ConvertTo-Json -Depth 10
