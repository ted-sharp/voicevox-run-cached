using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Synthesis;

public class VoiceVoxEngineManager : IDisposable
{
    private readonly VoiceVoxSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private Process? _engineProcess;
    private bool _disposed;

    public VoiceVoxEngineManager(VoiceVoxSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(AudioConstants.EngineHealthCheckTimeoutSeconds)
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (!_settings.KeepEngineRunning)
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
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
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
