# task uninstall / task uninstall:keep-data から呼ぶ。task install が入れたものを消す
# - 既定: exe・appsettings.json・cache\・logs\ を消し、空になったらフォルダも消す
#   (フォルダごと消さないのは、利用者が自分で置いたファイルまで消さないため)
# - -KeepData: exe と appsettings.json だけを消し、cache\ / logs\ は残す (設定を新しくして入れ直すとき)
# - Cache.Directory / Filler.Directory がインストール先の外なら、その場所は消さずに表示する
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VoicevoxRunCached'),
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-Host "Not installed: $InstallDir"
    exit 0
}
$InstallDir = (Resolve-Path -LiteralPath $InstallDir).Path

# appsettings.json を消す前に、インストール先の外にある保存先を調べる
function Get-OutsideDataDirectories([string]$settingsPath) {
    if (-not (Test-Path -LiteralPath $settingsPath)) {
        return @()
    }
    try {
        # 同梱の appsettings.json は行コメント (//) を使う。Windows PowerShell の ConvertFrom-Json はコメントを読めないので外す
        $json = (Get-Content -LiteralPath $settingsPath -Encoding UTF8 | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
        $settings = $json | ConvertFrom-Json
    }
    catch {
        return @("(appsettings.json を読めなかったため、Cache.Directory / Filler.Directory の場所は確かめていません)")
    }

    $outside = @()
    foreach ($section in 'Cache', 'Filler') {
        $entry = $settings.$section
        if (-not $entry -or -not $entry.Directory) {
            continue
        }
        $dir = [string]$entry.Directory
        if (-not [IO.Path]::IsPathRooted($dir)) {
            if ($entry.UseExecutableBaseDirectory -eq $true) {
                continue
            }
            $outside += "$section.Directory = $dir (実行したときの作業フォルダからの相対パス)"
            continue
        }
        $full = [IO.Path]::GetFullPath($dir).TrimEnd('\')
        if (-not $full.StartsWith($InstallDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $outside += "$section.Directory = $full"
        }
    }
    return $outside
}

$outsideDirs = if ($KeepData) { @() } else { @(Get-OutsideDataDirectories (Join-Path $InstallDir 'appsettings.json')) }

$names = @('VoicevoxRunCached.exe', 'appsettings.json')
if (-not $KeepData) {
    $names += 'cache', 'logs'
}

foreach ($name in $names) {
    $path = Join-Path $InstallDir $name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse
        Write-Host "Removed: $path"
    }
}

if (Get-ChildItem -LiteralPath $InstallDir -Force) {
    if ($KeepData) {
        Write-Host "Kept:    $InstallDir (cache\ and logs\ remain)"
    }
    else {
        Write-Host "Kept:    $InstallDir (contains files not installed by task install)"
    }
}
else {
    Remove-Item -LiteralPath $InstallDir
    Write-Host "Removed: $InstallDir"
}

if ($outsideDirs.Count -gt 0) {
    Write-Host ''
    Write-Host 'Not removed (outside the install folder). Delete them manually if no longer needed:'
    $outsideDirs | ForEach-Object { Write-Host "  $_" }
}
