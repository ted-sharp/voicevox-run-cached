using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class FillerManager
{
    private readonly FillerSettings _settings;
    private readonly AudioCacheManager _cacheManager;

    public FillerManager(FillerSettings settings, AudioCacheManager cacheManager)
    {
        this._settings = settings;
        this._cacheManager = cacheManager;
    }

    public async Task InitializeFillerCacheAsync(AppSettings appSettings)
    {
        if (!this._settings.Enabled)
            return;

        Directory.CreateDirectory(this._settings.Directory);

        using var spinner = new ProgressSpinner("Initializing filler cache...");
        using var apiClient = new VoiceVoxApiClient(appSettings.VoiceVox);

        await apiClient.InitializeSpeakerAsync(appSettings.VoiceVox.DefaultSpeaker);

        int processed = 0;
        int total = this._settings.FillerTexts.Length;

        foreach (var fillerText in this._settings.FillerTexts)
        {
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");

            var fillerRequest = new VoiceRequest
            {
                Text = fillerText,
                SpeakerId = appSettings.VoiceVox.DefaultSpeaker,
                Speed = 1.0,
                Pitch = 0.0,
                Volume = 1.0
            };

            // Check if already cached
            var cacheKey = this._cacheManager.ComputeCacheKey(ref fillerRequest);
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
            SpeakerId = 1, // Use default speaker for filler
            Speed = 1.0,
            Pitch = 0.0,
            Volume = 1.0
        };

        var cacheKey = this._cacheManager.ComputeCacheKey(ref fillerRequest);
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
        // Convert WAV to MP3 and save (similar to AudioCacheManager)
        var mp3Data = await this.ConvertWavToMp3Async(audioData);
        await File.WriteAllBytesAsync(filePath, mp3Data);
    }

    private async Task<byte[]> ConvertWavToMp3Async(byte[] wavData)
    {
        // Simple MP3 conversion - in a real implementation, you'd use NAudio.Lame
        // For now, we'll save as-is (assuming it's already in a suitable format)
        return await Task.FromResult(wavData);
    }
}
