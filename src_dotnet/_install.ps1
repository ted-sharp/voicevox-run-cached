# task install から呼ぶ。publish の出力を InstallDir にコピーする
# - appsettings.json は InstallDir に無いときだけコピーする (編集済みの設定を上書きしない)
# - cache\ / logs\ には触れない (更新してもキャッシュは残る)
# - PATH は変更せず、入っていなければ追加するコマンドを表示する

param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VoicevoxRunCached')
)

$ErrorActionPreference = 'Stop'

$exeName = 'VoicevoxRunCached.exe'
$settingsName = 'appsettings.json'

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path

# 実行中 (Hooks で読み上げ中など) だと上書きできずにここで止まる
Copy-Item -LiteralPath (Join-Path $SourceDir $exeName) -Destination $InstallDir -Force
Write-Host "Installed: $(Join-Path $InstallDir $exeName)"

$settingsPath = Join-Path $InstallDir $settingsName
if (Test-Path -LiteralPath $settingsPath) {
    Write-Host "Kept:      $settingsPath (not overwritten. New defaults: $(Join-Path $SourceDir $settingsName))"
}
else {
    Copy-Item -LiteralPath (Join-Path $SourceDir $settingsName) -Destination $InstallDir
    Write-Host "Installed: $settingsPath"
}

$pathEntries = @('User', 'Machine') |
    ForEach-Object { [Environment]::GetEnvironmentVariable('Path', $_) -split ';' } |
    Where-Object { $_ } |
    ForEach-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') }

if ($pathEntries -notcontains $InstallDir.TrimEnd('\')) {
    Write-Host ''
    Write-Host "$InstallDir is not in PATH. To add it, run this in PowerShell and reopen the terminal:"
    Write-Host "  [Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path', 'User') + ';$InstallDir', 'User')"
}
