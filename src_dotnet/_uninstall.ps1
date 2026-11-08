# task uninstall から呼ぶ。task install が入れた exe と appsettings.json を消す
# cache\ / logs\ は残す (消すなら --clear かフォルダごと手動で削除する)

param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VoicevoxRunCached')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-Host "Not installed: $InstallDir"
    exit 0
}

foreach ($name in 'VoicevoxRunCached.exe', 'appsettings.json') {
    $path = Join-Path $InstallDir $name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path
        Write-Host "Removed: $path"
    }
}

if (Get-ChildItem -LiteralPath $InstallDir -Force) {
    Write-Host "Kept:    $InstallDir (cache\ and logs\ remain. Delete the folder if no longer needed)"
}
else {
    Remove-Item -LiteralPath $InstallDir
    Write-Host "Removed: $InstallDir"
}
