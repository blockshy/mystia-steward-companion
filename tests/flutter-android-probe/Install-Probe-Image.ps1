#requires -Version 7.6
param([Parameter(Mandatory)][ValidateSet('api36-16k-x64-r7','api24-arm-r7','api30-x86-r16')][string]$Image,
    [Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (Test-Path $Directory) { throw 'Image directory exists' }
$entry = switch ($Image) {
    'api36-16k-x64-r7' { @{ url='https://dl.google.com/android/repository/sys-img/google_apis/x86_64-ps16k-36_r07.zip'; size=1849428145L; sha1='dd783282e84bf475a02eba6777c79fc5695e1583'; package='system-images;android-36;google_apis_ps16k;x86_64'; revision=7 } }
    'api24-arm-r7' { @{ url='https://dl.google.com/android/repository/sys-img/android/armeabi-v7a-24_r07.zip'; size=283677512L; sha1='3454546b4eed2d6c3dd06d47757d6da9f4176033'; package='system-images;android-24;default;armeabi-v7a'; revision=7 } }
    'api30-x86-r16' { @{ url='https://dl.google.com/android/repository/sys-img/google_apis/x86-30_r16.zip'; size=1240551553L; sha1='a58447e540a8581394dd04ee419c6771d62723d8'; package='system-images;android-30;google_apis;x86'; revision=16 } }
}
New-Item -ItemType Directory -Path $Directory | Out-Null
$archive = Join-Path $Directory 'official-image.zip'
& curl.exe --fail --location --retry 2 --output $archive $entry.url
if ($LASTEXITCODE -ne 0) { throw 'Official image download failed' }
if ((Get-Item $archive).Length -ne $entry.size -or (Get-FileHash $archive -Algorithm SHA1).Hash.ToLowerInvariant() -cne $entry.sha1) { throw 'Official image size/checksum mismatch' }
$entry.sha256 = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$entry.retrievedAt = [DateTime]::UtcNow.ToString('o')
$entry | ConvertTo-Json | Set-Content (Join-Path $Directory 'image-download.json') -Encoding utf8NoBOM
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $Directory 'image')
Get-Content (Join-Path $Directory 'image-download.json')
