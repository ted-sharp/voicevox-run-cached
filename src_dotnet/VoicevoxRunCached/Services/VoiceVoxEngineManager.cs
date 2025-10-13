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
        if (!_settings.KeepEngineRunning)
        {
            StopEngine();
        }
        _engineProcess?.Dispose();
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
        var possiblePaths = new List<string>();

        // Add paths based on engine type
        if (_settings.EngineType == EngineType.AivisSpeech)
        {
            possiblePaths.AddRange(GetAivisSpeechPaths());
        }
        else
        {
            possiblePaths.AddRange(GetVoiceVoxPaths());
        }

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

    private string[] GetVoiceVoxPaths()
    {
        return new[]
        {
            // User installation - vv-engine subfolder (current versions)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX", "VOICEVOX.exe"),

            // User AppData - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOICEVOX", "VOICEVOX.exe"),

            // Program Files - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "VOICEVOX.exe"),

            // Desktop (common for portable versions) - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VOICEVOX", "VOICEVOX.exe"),

            // Common download locations - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VOICEVOX", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VOICEVOX", "VOICEVOX.exe")
        };
    }

    private string[] GetAivisSpeechPaths()
    {
        return new[]
        {
            // User installation - vv-engine subfolder (current versions)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AivisSpeech", "AivisSpeech.exe"),

            // User AppData - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AivisSpeech", "AivisSpeech.exe"),

            // Program Files - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AivisSpeech", "AivisSpeech.exe"),

            // Desktop (common for portable versions) - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AivisSpeech", "AivisSpeech.exe"),

            // Common download locations - vv-engine subfolder
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AivisSpeech", "vv-engine", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AivisSpeech", "AivisSpeech.exe")
        };
    }
}
