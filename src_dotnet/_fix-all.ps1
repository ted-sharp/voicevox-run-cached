# Complete Code Quality Fix & Verification
# 全自動でコード品質を修正＆検証します

param(
    [switch]$CheckOnly = $false,
    [switch]$SkipSonar = $false,
    [switch]$SkipReSharper = $false,
    [switch]$Fast = $false,
    [string]$SonarUrl = $env:SONAR_HOST_URL,
    [string]$SonarToken = $env:SONAR_TOKEN,
    [string]$ProjectKey = "",
    [string]$ProjectName = ""
)

# Fast mode: Skip both ReSharper and SonarQube
if ($Fast) {
    $SkipReSharper = $true
    $SkipSonar = $true
}

# ソリューションファイルを自動検出
$solutionFile = Get-ChildItem -Path . -Filter *.sln | Select-Object -First 1
if (-not $solutionFile) {
    Write-Host "✗ Error: No solution file (.sln) found in current directory" -ForegroundColor Red
    exit 1
}

$solutionName = $solutionFile.Name
$solutionBaseName = $solutionFile.BaseName

# プロジェクトキーとプロジェクト名をソリューション名から自動生成（オーバーライドされていない場合）
if ([string]::IsNullOrEmpty($ProjectKey)) {
    $ProjectKey = $solutionBaseName.ToLower() -replace '\s+', '-'
}
if ([string]::IsNullOrEmpty($ProjectName)) {
    $ProjectName = $solutionBaseName
}

# デフォルト値
if ([string]::IsNullOrEmpty($SonarUrl)) {
    $SonarUrl = "http://localhost:9000"
}

# 統計情報
$script:ErrorCount = 0
$script:WarningCount = 0
$script:FixedFileCount = 0

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Complete Code Quality Fix" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Solution: $solutionName" -ForegroundColor Cyan
Write-Host "Project Key: $ProjectKey" -ForegroundColor Gray
Write-Host "Project Name: $ProjectName" -ForegroundColor Gray
Write-Host ""

if ($CheckOnly) {
    Write-Host "Mode: VERIFICATION ONLY (CI/PR Gate)" -ForegroundColor Yellow
} else {
    Write-Host "Mode: AUTO-FIX" -ForegroundColor Green
}

if ($SkipReSharper) { Write-Host "  - Skip ReSharper tools" -ForegroundColor Gray }
if ($SkipSonar) { Write-Host "  - Skip SonarQube analysis" -ForegroundColor Gray }
Write-Host ""

# ===========================================
# Step 1: ReSharper Cleanup (Auto-fix)
# ===========================================
if (-not $SkipReSharper -and -not $CheckOnly) {
    Write-Host "[1/6] ReSharper Cleanup..." -ForegroundColor Yellow

    $cleanupCode = Get-Command cleanupcode -ErrorAction SilentlyContinue
    if (-not $cleanupCode) {
        Write-Host "  ⚠ ReSharper CLI not found (skipping)" -ForegroundColor Yellow
        Write-Host "    Install: dotnet tool install -g JetBrains.ReSharper.GlobalTools" -ForegroundColor Gray
    } else {
        Write-Host "  Running cleanup..." -ForegroundColor Gray

        # DotSettings ファイルの確認/作成
        $settingsFile = "$solutionBaseName.sln.DotSettings"
        if (-not (Test-Path $settingsFile)) {
            $defaultSettings = @"
<wpf:ResourceDictionary xml:space="preserve" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:s="clr-namespace:System;assembly=mscorlib" xmlns:ss="urn:shemas-jetbrains-com:settings-storage-xaml" xmlns:wpf="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
	<s:String x:Key="/Default/CodeStyle/CodeCleanup/Profiles/=Default/@EntryIndexedValue">&lt;?xml version="1.0" encoding="utf-16"?&gt;&lt;Profile name="Default"&gt;&lt;CSReorderTypeMembers&gt;True&lt;/CSReorderTypeMembers&gt;&lt;CSUpdateFileHeader&gt;True&lt;/CSUpdateFileHeader&gt;&lt;CSOptimizeUsings&gt;&lt;OptimizeUsings&gt;True&lt;/OptimizeUsings&gt;&lt;/CSOptimizeUsings&gt;&lt;CSArrangeThisQualifier&gt;True&lt;/CSArrangeThisQualifier&gt;&lt;CSUseAutoProperty&gt;True&lt;/CSUseAutoProperty&gt;&lt;CSMakeFieldReadonly&gt;True&lt;/CSMakeFieldReadonly&gt;&lt;CSArrangeQualifiers&gt;True&lt;/CSArrangeQualifiers&gt;&lt;/Profile&gt;</s:String>
	<s:String x:Key="/Default/CodeStyle/CodeCleanup/SilentCleanupProfile/@EntryValue">Default</s:String>
</wpf:ResourceDictionary>
"@
            $defaultSettings | Out-File -FilePath $settingsFile -Encoding UTF8
        }

        cleanupcode $solutionName --profile=Default 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            $gitStatus = git status --short 2>$null | Where-Object { $_ -match '\.cs$' }
            $script:FixedFileCount += ($gitStatus | Measure-Object).Count
            Write-Host "  ✓ Cleanup completed" -ForegroundColor Green
        } else {
            Write-Host "  ✗ Cleanup failed" -ForegroundColor Red
            $script:ErrorCount++
        }
    }
    Write-Host ""
} elseif ($SkipReSharper -or $CheckOnly) {
    Write-Host "[1/6] ReSharper Cleanup (skipped)" -ForegroundColor Gray
    Write-Host ""
}

# ===========================================
# Step 2: dotnet format
# ===========================================
Write-Host "[2/6] dotnet format..." -ForegroundColor Yellow

if ($CheckOnly) {
    Write-Host "  Verifying formatting..." -ForegroundColor Gray
    dotnet format $solutionName --verify-no-changes --verbosity quiet
    $formatResult = $LASTEXITCODE

    if ($formatResult -eq 0) {
        Write-Host "  ✓ Format check passed" -ForegroundColor Green
    } else {
        Write-Host "  ✗ Format issues found" -ForegroundColor Red
        $script:ErrorCount++
    }
} else {
    Write-Host "  Formatting code..." -ForegroundColor Gray
    dotnet format $solutionName --verbosity quiet

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ Format completed" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ Some files could not be formatted" -ForegroundColor Yellow
        $script:WarningCount++
    }
}
Write-Host ""

# ===========================================
# Step 3: Build & Warnings
# ===========================================
Write-Host "[3/6] Build & Warnings..." -ForegroundColor Yellow
dotnet build $solutionName --configuration Release --verbosity quiet > $null 2>&1
$buildResult = $LASTEXITCODE

if ($buildResult -eq 0) {
    Write-Host "  ✓ Build succeeded" -ForegroundColor Green

    # 警告数をカウント
    $buildOutput = dotnet build $solutionName --configuration Release 2>&1
    $warnings = ($buildOutput | Select-String -Pattern "warning" -AllMatches).Matches.Count

    if ($warnings -eq 0) {
        Write-Host "  ✓ No warnings" -ForegroundColor Green
    } else {
        Write-Host "  ⚠ $warnings warning(s) found" -ForegroundColor Yellow
        $script:WarningCount += $warnings

        # Top 5 warnings
        $topWarnings = $buildOutput | Select-String -Pattern "warning" | Select-Object -First 5
        if ($topWarnings) {
            Write-Host "    Top warnings:" -ForegroundColor Gray
            $topWarnings | ForEach-Object {
                $line = $_.Line -replace '.*\\([^\\]+\.cs)\((\d+),\d+\): warning ([^:]+):.*', '$1:$2 - $3'
                Write-Host "      $line" -ForegroundColor DarkGray
            }
        }
    }
} else {
    Write-Host "  ✗ Build failed" -ForegroundColor Red
    $script:ErrorCount++
}
Write-Host ""

# ===========================================
# Step 4: ReSharper Inspection
# ===========================================
if (-not $SkipReSharper) {
    Write-Host "[4/6] ReSharper Inspection..." -ForegroundColor Yellow

    $inspectCode = Get-Command inspectcode -ErrorAction SilentlyContinue
    if (-not $inspectCode) {
        Write-Host "  ⚠ ReSharper CLI not found (skipping)" -ForegroundColor Yellow
        Write-Host "    Install: dotnet tool install -g JetBrains.ReSharper.GlobalTools" -ForegroundColor Gray
    } else {
        $outputDir = ".\quality-reports\resharper"
        if (-not (Test-Path $outputDir)) {
            New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
        }

        $outputFile = "$outputDir\inspection-$(Get-Date -Format 'yyyyMMdd-HHmmss').xml"

        Write-Host "  Running inspection..." -ForegroundColor Gray
        inspectcode $solutionName --output=$outputFile --format=Xml --no-build --severity=WARNING 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0 -and (Test-Path $outputFile)) {
            [xml]$results = Get-Content $outputFile
            $issues = $results.Report.Issues.Project.Issue

            if ($issues) {
                $errorCount = ($issues | Where-Object { $_.Severity -eq "ERROR" }).Count
                $warningCount = ($issues | Where-Object { $_.Severity -eq "WARNING" }).Count

                if ($errorCount -gt 0) {
                    Write-Host "  ✗ $errorCount error(s) found" -ForegroundColor Red
                    $script:ErrorCount += $errorCount
                } else {
                    Write-Host "  ✓ No errors" -ForegroundColor Green
                }

                if ($warningCount -gt 0) {
                    Write-Host "  ⚠ $warningCount warning(s) found" -ForegroundColor Yellow
                    $script:WarningCount += $warningCount
                }

                Write-Host "  Report: $outputFile" -ForegroundColor Gray
            } else {
                Write-Host "  ✓ No issues found" -ForegroundColor Green
            }
        } else {
            Write-Host "  ✗ Inspection failed" -ForegroundColor Red
            $script:ErrorCount++
        }
    }
    Write-Host ""
} else {
    Write-Host "[4/6] ReSharper Inspection (skipped)" -ForegroundColor Gray
    Write-Host ""
}

# ===========================================
# Step 5: SonarQube Analysis
# ===========================================
if (-not $SkipSonar) {
    Write-Host "[5/6] SonarQube Analysis..." -ForegroundColor Yellow

    if ([string]::IsNullOrEmpty($SonarToken)) {
        Write-Host "  ⚠ SONAR_TOKEN not set (skipping)" -ForegroundColor Yellow
        Write-Host '    Set: $env:SONAR_TOKEN = "your-token"' -ForegroundColor Gray
    } else {
        # SonarScanner インストールチェック
        $scannerCheck = dotnet tool list --global | Select-String "dotnet-sonarscanner"
        if ($null -eq $scannerCheck) {
            Write-Host "  Installing SonarScanner..." -ForegroundColor Gray
            dotnet tool install --global dotnet-sonarscanner 2>&1 | Out-Null
        }

        Write-Host "  Running analysis..." -ForegroundColor Gray

        # Begin
        dotnet sonarscanner begin `
            /k:$ProjectKey `
            /n:$ProjectName `
            /d:sonar.host.url=$SonarUrl `
            /d:sonar.token=$SonarToken `
            2>&1 | Out-Null

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ Failed to start analysis" -ForegroundColor Red
            $script:ErrorCount++
        } else {
            # Build
            dotnet build $solutionName --configuration Release --no-restore 2>&1 | Out-Null

            # End
            dotnet sonarscanner end /d:sonar.token=$SonarToken 2>&1 | Out-Null

            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Analysis completed" -ForegroundColor Green
                Write-Host "  Dashboard: $SonarUrl/dashboard?id=$ProjectKey" -ForegroundColor Cyan

                # レポート生成とローカル保存
                Start-Sleep -Seconds 5
                try {
                    $base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${SonarToken}:"))
                    $headers = @{ Authorization = "Basic $base64AuthInfo" }

                    # メトリクス取得
                    $metricsUrl = "$SonarUrl/api/measures/component?component=$ProjectKey&metricKeys=bugs,vulnerabilities,code_smells,coverage,duplicated_lines_density,ncloc"
                    $metricsResponse = Invoke-RestMethod -Uri $metricsUrl -Headers $headers -Method Get

                    $bugs = ($metricsResponse.component.measures | Where-Object { $_.metric -eq "bugs" }).value
                    $vulnerabilities = ($metricsResponse.component.measures | Where-Object { $_.metric -eq "vulnerabilities" }).value
                    $codeSmells = ($metricsResponse.component.measures | Where-Object { $_.metric -eq "code_smells" }).value

                    Write-Host "    Bugs: $bugs | Vulnerabilities: $vulnerabilities | Code Smells: $codeSmells" -ForegroundColor Gray

                    # ローカルレポート保存
                    $sonarOutputDir = ".\quality-reports\sonarqube"
                    if (-not (Test-Path $sonarOutputDir)) {
                        New-Item -ItemType Directory -Path $sonarOutputDir -Force | Out-Null
                    }

                    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

                    # メトリクスレポート保存
                    $metricsFile = "$sonarOutputDir\metrics-$timestamp.json"
                    $metricsResponse | ConvertTo-Json -Depth 10 | Out-File -FilePath $metricsFile -Encoding UTF8

                    # Issues取得と保存
                    $issuesUrl = "$SonarUrl/api/issues/search?componentKeys=$ProjectKey&resolved=false&ps=500"
                    $issuesResponse = Invoke-RestMethod -Uri $issuesUrl -Headers $headers -Method Get
                    $issuesFile = "$sonarOutputDir\issues-$timestamp.json"
                    $issuesResponse | ConvertTo-Json -Depth 10 | Out-File -FilePath $issuesFile -Encoding UTF8

                    Write-Host "    Reports saved:" -ForegroundColor Gray
                    Write-Host "      $metricsFile" -ForegroundColor DarkGray
                    Write-Host "      $issuesFile" -ForegroundColor DarkGray
                } catch {
                    Write-Host "    ⚠ Failed to save local reports: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            } else {
                Write-Host "  ✗ Analysis failed" -ForegroundColor Red
                $script:ErrorCount++
            }
        }
    }
    Write-Host ""
} else {
    Write-Host "[5/6] SonarQube Analysis (skipped)" -ForegroundColor Gray
    Write-Host ""
}

# ===========================================
# Step 6: Final Summary
# ===========================================
Write-Host "[6/6] Summary" -ForegroundColor Yellow

# 修正されたファイル
if (-not $CheckOnly) {
    $gitStatus = git status --short 2>$null | Where-Object { $_ -match '\.cs$' }
    $modifiedCount = ($gitStatus | Measure-Object).Count

    if ($modifiedCount -gt 0) {
        Write-Host "  Modified files: $modifiedCount" -ForegroundColor Cyan
        $gitStatus | Select-Object -First 5 | ForEach-Object {
            Write-Host "    $_" -ForegroundColor Gray
        }
        if ($modifiedCount -gt 5) {
            Write-Host "    ... and $($modifiedCount - 5) more" -ForegroundColor Gray
        }
    } else {
        Write-Host "  No files were modified" -ForegroundColor Green
    }
    Write-Host ""
}

# コード統計
Write-Host "  Code statistics:" -ForegroundColor Cyan
$csFiles = Get-ChildItem -Path . -Recurse -Include *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
$totalFiles = $csFiles.Count
$totalLines = 0
foreach ($file in $csFiles) {
    $totalLines += (Get-Content $file.FullName -ErrorAction SilentlyContinue).Count
}
Write-Host "    Files: $totalFiles | Lines: $totalLines" -ForegroundColor Gray
Write-Host ""

# 最終結果
Write-Host "========================================" -ForegroundColor Cyan
if ($script:ErrorCount -eq 0 -and $script:WarningCount -eq 0) {
    Write-Host "  ✓ ALL CHECKS PASSED" -ForegroundColor Green
} elseif ($script:ErrorCount -eq 0) {
    Write-Host "  ⚠ COMPLETED WITH WARNINGS" -ForegroundColor Yellow
    Write-Host "    Warnings: $($script:WarningCount)" -ForegroundColor Yellow
} else {
    Write-Host "  ✗ FAILED" -ForegroundColor Red
    Write-Host "    Errors: $($script:ErrorCount)" -ForegroundColor Red
    Write-Host "    Warnings: $($script:WarningCount)" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if (-not $CheckOnly -and $script:ErrorCount -eq 0) {
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Review changes: git diff" -ForegroundColor Gray
    Write-Host "  2. Commit changes: git add . && git commit" -ForegroundColor Gray
    Write-Host ""
}

# 終了コード
if ($script:ErrorCount -gt 0) {
    exit 1
} else {
    exit 0
}
