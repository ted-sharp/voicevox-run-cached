using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Synthesis;

public class VoiceVoxEngineManager : IDisposable
{
    private readonly VoiceVoxSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    // 自プロセスから起動したエンジンプロセス。外部で既に動いていたエンジンを発見した場合は null。
    // この区別は Dispose 時の停止対象を「自分が起こしたものだけ」に絞るために必要。
    private Process? _engineProcess;
    private bool _disposed;

    public VoiceVoxEngineManager(VoiceVoxSettings settings, ILogger logger, IClock clock)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(AudioConstants.EngineHealthCheckTimeoutSeconds)
        };
    }

    /// <summary>
    /// このプロセスがエンジンを起動した場合に true。外部で既に動いていたエンジンを利用している間は false。
    /// </summary>
    private bool StartedByUs => _engineProcess != null;

    public void Dispose()
    {
        if (_disposed)
            return;

        // KeepEngineRunning=true ならこのプロセスが起動した子も生かしておく (常駐運用)。
        // false なら自分で起動したものだけ止める (外部起動エンジンには触れない)。
        if (!_settings.KeepEngineRunning && StartedByUs)
        {
            StopEngine();
        }

        _engineProcess?.Dispose();
        _httpClient.Dispose();
        _disposed = true;
    }

    public async Task<bool> EnsureEngineRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.AutoStartEngine)
            return await IsEngineRunningAsync(cancellationToken);

        if (await IsEngineRunningAsync(cancellationToken))
            return true;

        _logger.LogInformation("VOICEVOXエンジンが検出されていません。起動を試行します...");
        return await StartEngineAsync(cancellationToken);
    }

    /// <summary>
    /// エンジン起動を確認し、失敗時は <see cref="VoicevoxRunCachedException"/> を投げる。
    /// 各コマンドハンドラの定型例外スローを統合するためのエントリポイント。
    /// </summary>
    public async Task EnsureRunningOrThrowAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureEngineRunningAsync(cancellationToken))
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Engine.EngineNotAvailable,
                message: "VOICEVOX engine is not available",
                userMessage: "VOICEVOXエンジンが利用できません。",
                suggestedSolution: "VOICEVOXエンジンが起動しているか、appsettings.json の EnginePath が正しいか確認してください。");
        }
    }

    private async Task<bool> IsEngineRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_settings.BaseUrl}/version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> StartEngineAsync(CancellationToken cancellationToken)
    {
        string enginePath = _settings.EnginePath;

        if (String.IsNullOrEmpty(enginePath))
        {
            enginePath = FindDefaultEnginePath();
            if (String.IsNullOrEmpty(enginePath))
            {
                _logger.LogError("VOICEVOXエンジンが見つかりません。appsettings.jsonでEnginePathを設定してください");
                return false;
            }
        }

        if (!File.Exists(enginePath))
        {
            _logger.LogError("VOICEVOXエンジンが見つかりません: {EnginePath}", enginePath);
            return false;
        }

        try
        {
            using var spinner = new ProgressSpinner("Starting VOICEVOX engine...");

            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = _settings.EngineArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = Path.GetDirectoryName(enginePath) ?? Environment.CurrentDirectory
            };

            _engineProcess = Process.Start(startInfo);
            if (_engineProcess == null)
            {
                _logger.LogError("VOICEVOXエンジンプロセスの起動に失敗しました");
                return false;
            }

            var timeout = TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds);
            var startTime = _clock.UtcNow;

            while (_clock.UtcNow - startTime < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await IsEngineRunningAsync(cancellationToken))
                {
                    _logger.LogInformation("VOICEVOXエンジンが正常に起動しました");
                    return true;
                }

                await Task.Delay(AudioConstants.EngineStartupPollIntervalMs, cancellationToken);
            }

            _logger.LogError("VOICEVOXエンジンの起動タイムアウトが発生しました");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C は起動失敗 (EngineNotAvailable) にせず、キャンセルとして呼び出し元へ伝える
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VOICEVOXエンジンの起動中にエラーが発生しました");
            return false;
        }
    }

    public void StopEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            try
            {
                // VOICEVOX/AivisSpeech は run.exe から uvicorn worker 等の子プロセスを spawn するため、
                // 親だけ Kill するとワーカープロセスがゾンビ化する。
                _engineProcess.Kill(entireProcessTree: true);
                _engineProcess.WaitForExit(AudioConstants.EngineStopWaitTimeoutMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOICEVOXエンジンの停止に失敗しました");
            }
        }
    }

    private string FindDefaultEnginePath()
    {
        var productName = _settings.EngineType == EngineType.AivisSpeech ? "AivisSpeech" : "VOICEVOX";
        var possiblePaths = GetEnginePaths(productName);

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                _logger.LogInformation("{EngineType} エンジンが見つかりました: {Path}", _settings.EngineType, path);
                return path;
            }
        }

        return String.Empty;
    }

    private static IEnumerable<string> GetEnginePaths(string productName)
    {
        var basePaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        var relativePaths = new[]
        {
            Path.Combine(productName, "vv-engine", "run.exe"),
            Path.Combine(productName, "run.exe"),
            Path.Combine(productName, $"{productName}.exe")
        };

        foreach (var basePath in basePaths)
        {
            foreach (var relativePath in relativePaths)
            {
                yield return Path.Combine(basePath, relativePath);
            }
        }
    }
}
