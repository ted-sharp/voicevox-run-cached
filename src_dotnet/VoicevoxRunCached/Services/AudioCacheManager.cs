using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Lame;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class AudioCacheManager
{
    private readonly CacheSettings _settings;
    private readonly Lock _cacheLock = new();

    public AudioCacheManager(CacheSettings settings)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.EnsureCacheDirectoryExists();
    }

    public async Task<byte[]?> GetCachedAudioAsync(VoiceRequest request)
    {
        var cacheKey = this.ComputeCacheKey(ref request);
        var audioFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.meta.json");

        lock (this._cacheLock)
        {
            if (!File.Exists(audioFilePath) || !File.Exists(metaFilePath))
            {
                return null;
            }
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaFilePath);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

            if (metadata == null || this.IsExpired(metadata.CreatedAt))
            {
                await this.DeleteCacheFileAsync(cacheKey);
                return null;
            }

            return await File.ReadAllBytesAsync(audioFilePath);
        }
        catch (Exception)
        {
            await this.DeleteCacheFileAsync(cacheKey);
            return null;
        }
    }

    public Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = this.ComputeCacheKey(ref request);
        var audioFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.meta.json");

        try
        {
            // Convert WAV to MP3
            var mp3Data = this.ConvertWavToMp3(audioData);

            lock (this._cacheLock)
            {
                File.WriteAllBytes(audioFilePath, mp3Data);

                var metadata = new CacheMetadata
                {
                    CreatedAt = DateTime.UtcNow,
                    Text = request.Text,
                    SpeakerId = request.SpeakerId,
                    Speed = request.Speed,
                    Pitch = request.Pitch,
                    Volume = request.Volume
                };

                var metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(metaFilePath, metaJson);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save audio cache: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public async Task CleanupExpiredCacheAsync()
    {
        if (!Directory.Exists(this._settings.Directory))
        {
            return;
        }

        try
        {
            var metaFiles = Directory.GetFiles(this._settings.Directory, "*.meta.json");
            var expiredFiles = new List<string>();

            foreach (var metaFile in metaFiles)
            {
                try
                {
                    var metaJson = await File.ReadAllTextAsync(metaFile);
                    var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

                    if (metadata == null || this.IsExpired(metadata.CreatedAt))
                    {
                        var cacheKey = Path.GetFileNameWithoutExtension(metaFile).Replace(".meta", "");
                        expiredFiles.Add(cacheKey);
                    }
                }
                catch
                {
                    var cacheKey = Path.GetFileNameWithoutExtension(metaFile).Replace(".meta", "");
                    expiredFiles.Add(cacheKey);
                }
            }

            foreach (var cacheKey in expiredFiles)
            {
                await this.DeleteCacheFileAsync(cacheKey);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to cleanup expired cache: {ex.Message}", ex);
        }
    }

    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(VoiceRequest request)
    {
        var segments = TextSegmentProcessor.SegmentText(request.Text);

        // Check cache for each segment
        foreach (var segment in segments)
        {
            var segmentRequest = new VoiceRequest
            {
                Text = segment.Text,
                SpeakerId = request.SpeakerId,
                Speed = request.Speed,
                Pitch = request.Pitch,
                Volume = request.Volume
            };

            var cachedData = await this.GetCachedAudioAsync(segmentRequest);
            if (cachedData != null)
            {
                segment.IsCached = true;
                segment.AudioData = cachedData;
            }
        }

        return segments;
    }

    // C# 13 ref readonly parameter for better performance with large structs
    public string ComputeCacheKey(ref readonly VoiceRequest request)
    {
        var keyString = $"{request.Text}|{request.SpeakerId}|{request.Speed:F2}|{request.Pitch:F2}|{request.Volume:F2}";
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(this._settings.Directory))
        {
            Directory.CreateDirectory(this._settings.Directory);
        }
    }

    private bool IsExpired(DateTime createdAt)
    {
        return DateTime.UtcNow - createdAt > TimeSpan.FromDays(this._settings.ExpirationDays);
    }

    private byte[] ConvertWavToMp3(byte[] wavData)
    {
        try
        {
            using var wavStream = new MemoryStream(wavData);
            using var waveReader = new WaveFileReader(wavStream);
            using var outputStream = new MemoryStream();

            MediaFoundationEncoder.EncodeToMp3(waveReader, outputStream, 128000);
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert WAV to MP3: {ex.Message}", ex);
        }
    }

    private Task DeleteCacheFileAsync(string cacheKey)
    {
        try
        {
            var audioFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
            var metaFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.meta.json");

            lock (this._cacheLock)
            {
                if (File.Exists(audioFilePath))
                {
                    File.Delete(audioFilePath);
                }

                if (File.Exists(metaFilePath))
                {
                    File.Delete(metaFilePath);
                }
            }
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    public async Task ClearAllCacheAsync()
    {
        try
        {
            if (!Directory.Exists(this._settings.Directory))
            {
                return;
            }

            var audioFiles = Directory.GetFiles(this._settings.Directory, "*.mp3");
            var metaFiles = Directory.GetFiles(this._settings.Directory, "*.meta.json");

            lock (this._cacheLock)
            {
                foreach (var file in audioFiles.Concat(metaFiles))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }

            // Also clear filler cache directory if it exists
            var fillerDirectory = Path.Combine(Path.GetDirectoryName(this._settings.Directory) ?? ".", "filler");
            if (Directory.Exists(fillerDirectory))
            {
                var fillerFiles = Directory.GetFiles(fillerDirectory, "*.mp3");
                lock (this._cacheLock)
                {
                    foreach (var file in fillerFiles)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // Ignore individual file deletion errors
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to clear cache: {ex.Message}", ex);
        }
    }
}

public class CacheMetadata
{
    public DateTime CreatedAt { get; set; }
    public string Text { get; set; } = String.Empty;
    public int SpeakerId { get; set; }
    public double Speed { get; set; }
    public double Pitch { get; set; }
    public double Volume { get; set; }
}
