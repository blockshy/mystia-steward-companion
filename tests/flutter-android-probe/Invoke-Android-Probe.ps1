#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Adb,
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$ApkSha256,
    [Parameter(Mandatory)][string]$SourceDigest,
    [string]$PreviousApkSha256 = '',
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$Endpoint,
    [Parameter(Mandatory)][string]$Nonce,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Baseline','Restricted','Granted','Restored')][string]$Mode = 'Baseline',
    [switch]$Install,
    [switch]$CaptureWindowState
)
$ErrorActionPreference = 'Stop'
$id = 'com.tyukki.mystia.steward.companion.p0probe'
if ($Mode -ne 'Baseline' -and $Serial -notmatch '^emulator-[0-9]+$') { throw 'Permission/reboot experiments are restricted to an isolated emulator' }
if ($RunId -cnotmatch '^[a-z0-9][a-z0-9-]{7,63}$' -or $Nonce -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid identity' }
if ($Endpoint -cnotmatch '^http://192\.168\.[0-9]{1,3}\.[0-9]{1,3}:[0-9]{4,5}/probe$') { throw 'Only explicit LAN fixture endpoints are allowed' }
if ((Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ApkSha256) { throw 'APK hash mismatch' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Evidence directory already exists' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
function Invoke-Adb {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    $output = & $Adb -s $Serial @Arguments 2>&1
    $code = $LASTEXITCODE
    [IO.File]::AppendAllText((Join-Path $OutputDirectory 'adb.log'), "adb $($Arguments -join ' ')`n$($output -join "`n")`nexit=$code`n")
    if ($code -ne 0) { throw "adb failed ($code): $($Arguments[0])" }
    return ($output -join "`n").Trim()
}
$existing = Invoke-Adb @('shell','pm','list','packages',$id)
if ($Install) {
    if ($existing) { throw 'Probe already installed; refusing to replace an unknown existing package' }
    $installed = Invoke-Adb @('install','--no-streaming',$Apk)
    if ($installed -notmatch 'Success') { throw 'Install did not return Success' }
} elseif (-not $existing) { throw 'Probe package is absent' }
$installedPath = Invoke-Adb @('shell','pm','path',$id)
if ($installedPath -cnotmatch '^package:(/data/app/[A-Za-z0-9_+./=~-]+/base\.apk)$') { throw 'Unexpected installed APK layout' }
$installedHash = Invoke-Adb @('shell','sha256sum',$Matches[1])
if ($PreviousApkSha256) {
    if ($Install -or $PreviousApkSha256 -cnotmatch '^[a-f0-9]{64}$' -or -not $installedHash.StartsWith($PreviousApkSha256 + ' ')) { throw 'Previous isolated probe APK identity mismatch' }
    $updated = Invoke-Adb @('install','--no-streaming','-r',$Apk)
    if ($updated -notmatch 'Success') { throw 'Probe update did not return Success' }
    $installedPath = Invoke-Adb @('shell','pm','path',$id)
    if ($installedPath -cnotmatch '^package:(/data/app/[A-Za-z0-9_+./=~-]+/base\.apk)$') { throw 'Unexpected updated APK layout' }
    $installedHash = Invoke-Adb @('shell','sha256sum',$Matches[1])
}
if (-not $installedHash.StartsWith($ApkSha256 + ' ')) { throw 'Installed APK differs from the verified build' }
$facts = [ordered]@{ schemaVersion=1; runId=$RunId; mode=$Mode; apkSha256=$ApkSha256
    deviceAbi=(Invoke-Adb @('shell','getprop','ro.product.cpu.abilist'))
    sdk=(Invoke-Adb @('shell','getprop','ro.build.version.sdk'))
    pageSize=(Invoke-Adb @('shell','getconf','PAGE_SIZE'))
    fingerprint=(Invoke-Adb @('shell','getprop','ro.build.fingerprint')) }
if ($Mode -eq 'Restricted') {
    Invoke-Adb @('shell','am','compat','enable','RESTRICT_LOCAL_NETWORK',$id) | Out-Null
    Invoke-Adb @('shell','pm','revoke',$id,'android.permission.NEARBY_WIFI_DEVICES') | Out-Null
    Invoke-Adb @('reboot') | Out-Null
    $bootDeadline = [DateTime]::UtcNow.AddSeconds(180)
    do {
        Start-Sleep -Milliseconds 1000
        $boot = & $Adb -s $Serial shell getprop sys.boot_completed 2>$null
        if ($LASTEXITCODE -eq 0 -and "$boot".Trim() -eq '1') { break }
    } while ([DateTime]::UtcNow -lt $bootDeadline)
    if ("$boot".Trim() -ne '1') { throw 'Isolated emulator did not finish reboot' }
} elseif ($Mode -eq 'Granted') {
    Invoke-Adb @('shell','pm','grant',$id,'android.permission.NEARBY_WIFI_DEVICES') | Out-Null
} elseif ($Mode -eq 'Restored') {
    Invoke-Adb @('shell','am','compat','reset','RESTRICT_LOCAL_NETWORK',$id) | Out-Null
    Invoke-Adb @('shell','pm','revoke',$id,'android.permission.NEARBY_WIFI_DEVICES') | Out-Null
}
Invoke-Adb @('shell','am','force-stop',$id) | Out-Null
$denied = if ($Mode -eq 'Restricted') { 'true' } else { 'false' }
# A renderer failure can leave am start -W waiting indefinitely. The bound below
# waits for the application's actual first-frame/report evidence instead.
Invoke-Adb @('shell','am','start','-n',"$id/.MainActivity",'--es','run_id',$RunId,'--es','endpoint',$Endpoint,'--es','nonce',$Nonce,'--ez','expect_denied',$denied) | Out-Null
$remote = "/sdcard/Android/data/$id/files/$RunId.json"
$deadline = [DateTime]::UtcNow.AddSeconds(90)
do {
    & $Adb -s $Serial shell test -f $remote 2>$null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $deadline)
Invoke-Adb @('pull',$remote,(Join-Path $OutputDirectory 'probe-result.json')) | Out-Null
$report = Get-Content (Join-Path $OutputDirectory 'probe-result.json') -Raw | ConvertFrom-Json
if ($report.runId -cne $RunId -or $report.nonce -cne $Nonce -or $report.sourceDigest -cne $SourceDigest) { throw 'Stale or unrelated report/source identity' }
$facts.reportSha256 = (Get-FileHash (Join-Path $OutputDirectory 'probe-result.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$facts.passed = $report.passed -eq $true -and $report.checks.Count -ge 12 -and @($report.checks | Where-Object passed -ne $true).Count -eq 0
if ($CaptureWindowState) {
    $windowState = Invoke-Adb @('shell','dumpsys','window','windows')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'window-state.txt'), $windowState)
    $facts.windowStateSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'window-state.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
}
Invoke-Adb @('shell','am','force-stop',$id) | Out-Null
$facts | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutputDirectory 'runtime-evidence.json') -Encoding utf8NoBOM
if (-not $facts.passed) { throw 'Android runtime probe has failing checks' }
$facts | ConvertTo-Json -Depth 10
