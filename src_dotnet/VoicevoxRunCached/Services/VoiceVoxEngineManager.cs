using System.Diagnostics;
using System.Net.Http;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

public class VoiceVoxEngineManager : IDisposable
{
    private readonly VoiceVoxSettings _settings;
    private Process? _engineProcess;

    public VoiceVoxEngineManager(VoiceVoxSettings settings)
    {
        this._settings = settings;
    }

    public async Task<bool> EnsureEngineRunningAsync()
    {
        if (!this._settings.AutoStartEngine)
            return await this.IsEngineRunningAsync();

        if (await this.IsEngineRunningAsync())
            return true;

        Console.WriteLine("VOICEVOX engine not detected, attempting to start...");
        return await this.StartEngineAsync();
    }

    private async Task<bool> IsEngineRunningAsync()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
            var response = await client.GetAsync($"{this._settings.BaseUrl}/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> StartEngineAsync()
    {
        string enginePath = this._settings.EnginePath;

        // If no path specified, try to find default installation
        if (String.IsNullOrEmpty(enginePath))
        {
            enginePath = this.FindDefaultEnginePath();
            if (String.IsNullOrEmpty(enginePath))
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
                Arguments = this._settings.EngineArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = Path.GetDirectoryName(enginePath) ?? Environment.CurrentDirectory
            };

            this._engineProcess = Process.Start(startInfo);
            if (this._engineProcess == null)
            {
                Console.WriteLine("\e[31mError: Failed to start VOICEVOX engine process\e[0m");
                return false;
            }

            // Wait for engine to be ready
            var timeout = TimeSpan.FromSeconds(this._settings.StartupTimeoutSeconds);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow - startTime < timeout)
            {
                if (await this.IsEngineRunningAsync())
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
        if (this._engineProcess != null && !this._engineProcess.HasExited)
        {
            try
            {
                this._engineProcess.Kill();
                this._engineProcess.WaitForExit(5000);
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
        if (this._settings.EngineType == EngineType.AivisSpeech)
        {
            possiblePaths.AddRange(this.GetAivisSpeechPaths());
        }
        else
        {
            possiblePaths.AddRange(this.GetVoiceVoxPaths());
        }

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                Console.WriteLine($"Found {this._settings.EngineType} engine at: {path}");
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

    public void Dispose()
    {
        if (!this._settings.KeepEngineRunning)
        {
            this.StopEngine();
        }
        this._engineProcess?.Dispose();
    }
}
