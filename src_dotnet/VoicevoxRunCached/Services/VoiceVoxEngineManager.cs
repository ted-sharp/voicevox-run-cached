using System.Diagnostics;
using System.Net.Http;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

public class VoiceVoxEngineManager
{
    private readonly VoiceVoxSettings _settings;
    private Process? _engineProcess;

    public VoiceVoxEngineManager(VoiceVoxSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> EnsureEngineRunningAsync()
    {
        if (!_settings.AutoStartEngine)
            return await IsEngineRunningAsync();

        if (await IsEngineRunningAsync())
            return true;

        Console.WriteLine("VOICEVOX engine not detected, attempting to start...");
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
        if (string.IsNullOrEmpty(enginePath))
        {
            enginePath = FindDefaultEnginePath();
            if (string.IsNullOrEmpty(enginePath))
            {
                Console.WriteLine("\e[31mError: VOICEVOX engine not found. Please set EnginePath in appsettings.json\e[0m");
                return false;
            }
        }

        if (!File.Exists(enginePath))
        {
            Console.WriteLine($"\e[31mError: VOICEVOX engine not found at: {enginePath}\e[0m");
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
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _engineProcess = Process.Start(startInfo);
            if (_engineProcess == null)
            {
                Console.WriteLine("\e[31mError: Failed to start VOICEVOX engine process\e[0m");
                return false;
            }

            // Wait for engine to be ready
            var timeout = TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
            {
                if (await IsEngineRunningAsync())
                {
                    spinner.Dispose();
                    Console.WriteLine("\e[32mVOICEVOX engine started successfully\e[0m");
                    return true;
                }

                await Task.Delay(1000);
            }

            spinner.Dispose();
            Console.WriteLine("\e[31mError: VOICEVOX engine startup timeout\e[0m");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\e[31mError starting VOICEVOX engine: {ex.Message}\e[0m");
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
                Console.WriteLine($"Warning: Failed to stop VOICEVOX engine: {ex.Message}");
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
                Console.WriteLine($"Found {_settings.EngineType} engine at: {path}");
                return path;
            }
        }

        return string.Empty;
    }

    private string[] GetVoiceVoxPaths()
    {
        return new[]
        {
            // User installation (Microsoft Store or portable)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX", "run.exe"),
            
            // User AppData
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VOICEVOX", "run.exe"),
            
            // Program Files
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VOICEVOX", "run.exe"),
            
            // Desktop (common for portable versions)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VOICEVOX", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "VOICEVOX", "run.exe"),
            
            // Common download locations
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VOICEVOX", "VOICEVOX.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "VOICEVOX", "run.exe")
        };
    }

    private string[] GetAivisSpeechPaths()
    {
        return new[]
        {
            // User installation
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AivisSpeech", "run.exe"),
            
            // User AppData
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AivisSpeech", "run.exe"),
            
            // Program Files
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AivisSpeech", "run.exe"),
            
            // Desktop (common for portable versions)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AivisSpeech", "run.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AivisSpeech", "run.exe"),
            
            // Common download locations
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AivisSpeech", "AivisSpeech.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AivisSpeech", "run.exe")
        };
    }

    public void Dispose()
    {
        StopEngine();
        _engineProcess?.Dispose();
    }
}