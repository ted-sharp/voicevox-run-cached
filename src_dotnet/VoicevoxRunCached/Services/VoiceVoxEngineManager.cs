using System.Diagnostics;
using Serilog;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

public class VoiceVoxEngineManager : IDisposable
{
    private readonly VoiceVoxSettings _settings;
    private Process? _engineProcess;

    public VoiceVoxEngineManager(VoiceVoxSettings settings)
    {
        _settings = settings;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!_settings.KeepEngineRunning)
            {
                StopEngine();
            }
            _engineProcess?.Dispose();
        }
    }

    public async Task<bool> EnsureEngineRunningAsync()
    {
        if (!_settings.AutoStartEngine)
            return await IsEngineRunningAsync();

        if (await IsEngineRunningAsync())
            return true;

        Log.Information("VOICEVOXエンジンが検出されていません。起動を試行します...");
        return await StartEngineAsync();
    }

    private async Task<bool> IsEngineRunningAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{_settings.BaseUrl}/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> StartEngineAsync()
    {
        string enginePath = _settings.EnginePath;

        // If no path specified, try to find default installation
        if (String.IsNullOrEmpty(enginePath))
        {
            enginePath = FindDefaultEnginePath();
            if (String.IsNullOrEmpty(enginePath))
            {
                Log.Error("VOICEVOXエンジンが見つかりません。appsettings.jsonでEnginePathを設定してください");
                return false;
            }
        }

        if (!File.Exists(enginePath))
        {
            Log.Error("VOICEVOXエンジンが見つかりません: {EnginePath}", enginePath);
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
                Log.Error("VOICEVOXエンジンプロセスの起動に失敗しました");
                return false;
            }

            // Wait for engine to be ready
            var timeout = TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
            {
                if (await IsEngineRunningAsync())
                {
                    Log.Information("VOICEVOXエンジンが正常に起動しました");
                    return true;
                }

                await Task.Delay(1000);
            }

            Log.Error("VOICEVOXエンジンの起動タイムアウトが発生しました");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "VOICEVOXエンジンの起動中にエラーが発生しました");
            return false;
        }
    }

    public void StopEngine()
    {
        if (_engineProcess != null && !_engineProcess.HasExited)
        {
            try
            {
                _engineProcess.Kill();
                _engineProcess.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "VOICEVOXエンジンの停止に失敗しました");
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
                Log.Information("{EngineType} エンジンが見つかりました: {Path}", _settings.EngineType, path);
                return path;
            }
        }

        return String.Empty;
    }

    private IEnumerable<string> GetEnginePaths(string productName)
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
