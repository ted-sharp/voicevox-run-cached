using NAudio.Wave;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class FillerManager
{
    private readonly FillerSettings _settings;
    private readonly AudioCacheManager _cacheManager;
    private readonly int _defaultSpeaker;

    public FillerManager(FillerSettings settings, AudioCacheManager cacheManager, int defaultSpeakerId)
    {
        this._settings = settings;
        this._cacheManager = cacheManager;
        this._defaultSpeaker = defaultSpeakerId;
        this.ResolveFillerBaseDirectory();
    }

    public async Task InitializeFillerCacheAsync(AppSettings appSettings)
    {
        if (!this._settings.Enabled)
            return;

        Directory.CreateDirectory(this._settings.Directory);

        using var spinner = new ProgressSpinner("Initializing filler cache...");
        using var apiClient = new VoiceVoxApiClient(appSettings.VoiceVox);

        await apiClient.InitializeSpeakerAsync(this._defaultSpeaker);

        int processed = 0;
        int total = this._settings.FillerTexts.Length;

        foreach (var fillerText in this._settings.FillerTexts)
        {
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");

            var fillerRequest = new VoiceRequest
            {
                Text = fillerText,
                SpeakerId = this._defaultSpeaker,
                Speed = 1.0,
                Pitch = 0.0,
                Volume = 1.0
            };

            // Check if already cached
            var cacheKey = this._cacheManager.ComputeCacheKey(fillerRequest);
            var fillerCachePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");

            if (!File.Exists(fillerCachePath))
            {
                try
                {
                    var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest);
                    var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId);

                    // Save directly to filler directory
                    await this.SaveFillerAudioAsync(fillerCachePath, audioData);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to generate filler '{fillerText}': {ex.Message}");
                }
            }
        }

        spinner.Dispose();
        Console.WriteLine($"\e[32mFiller cache initialized with {this._settings.FillerTexts.Length} items\e[0m");
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync()
    {
        if (!this._settings.Enabled || this._settings.FillerTexts.Length == 0)
            return null;

        var random = new Random();
        var randomFiller = this._settings.FillerTexts[random.Next(this._settings.FillerTexts.Length)];

        var fillerRequest = new VoiceRequest
        {
            Text = randomFiller,
            SpeakerId = this._defaultSpeaker,
            Speed = 1.0,
            Pitch = 0.0,
            Volume = 1.0
        };

        var cacheKey = this._cacheManager.ComputeCacheKey(fillerRequest);
        var fillerCachePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");

        if (File.Exists(fillerCachePath))
        {
            try
            {
                return await File.ReadAllBytesAsync(fillerCachePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load filler audio: {ex.Message}");
            }
        }

        return null;
    }

    private async Task SaveFillerAudioAsync(string filePath, byte[] audioData)
    {
        var mp3Data = await this.ConvertWavToMp3Async(audioData);
        await File.WriteAllBytesAsync(filePath, mp3Data);
    }

    private async Task<byte[]> ConvertWavToMp3Async(byte[] wavData)
    {
        try
        {
            using var wavStream = new MemoryStream(wavData);
            using var waveReader = new WaveFileReader(wavStream);
            using var outputStream = new MemoryStream();
            MediaFoundationManager.EnsureInitialized();
            MediaFoundationEncoder.EncodeToMp3(waveReader, outputStream, 128000);
            return await Task.FromResult(outputStream.ToArray());
        }
        catch
        {
            // フォーマット判定に失敗した場合はそのまま返す（再生側でフォールバック）
            return await Task.FromResult(wavData);
        }
    }

    public Task ClearFillerCacheAsync()
    {
        try
        {
            if (!Directory.Exists(this._settings.Directory))
                return Task.CompletedTask;

            var files = Directory.GetFiles(this._settings.Directory, "*.mp3");
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    private void ResolveFillerBaseDirectory()
    {
        try
        {
            if (this._settings.UseExecutableBaseDirectory && !Path.IsPathRooted(this._settings.Directory))
            {
                var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
                var combined = Path.Combine(executableDirectory, this._settings.Directory);
                this._settings.Directory = Path.GetFullPath(combined);
            }
        }
        catch
        {
        }
    }
}
