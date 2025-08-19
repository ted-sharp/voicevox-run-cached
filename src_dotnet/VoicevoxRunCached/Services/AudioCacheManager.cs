using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Lame;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using Serilog;

namespace VoicevoxRunCached.Services;

public class AudioCacheManager : IDisposable
{
    private readonly CacheSettings _settings;
    private readonly MemoryCacheService _memoryCache;
    // Switch to new System.Threading.Lock and use C# 13 lock statement
    private readonly Lock _cacheLock = new();

    public AudioCacheManager(CacheSettings settings, MemoryCacheService? memoryCache = null)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._memoryCache = memoryCache ?? new MemoryCacheService(settings);
        this.ResolveCacheBaseDirectory();
        this.EnsureCacheDirectoryExists();

        Log.Information("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効", this._settings.Directory);
    }

    public async Task<byte[]?> GetCachedAudioAsync(VoiceRequest request)
    {
        var cacheKey = this.ComputeCacheKey(request);

        // まずメモリキャッシュをチェック
        var memoryCached = this._memoryCache.Get<byte[]>(cacheKey);
        if (memoryCached != null)
        {
            Log.Debug("メモリキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, memoryCached.Length);
            return memoryCached;
        }

        // メモリキャッシュにない場合はディスクキャッシュをチェック
        var audioFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.meta.json");

        // ファイル存在チェックを一度に実行
        bool audioExists, metaExists;
        lock (this._cacheLock)
        {
            audioExists = File.Exists(audioFilePath);
            metaExists = File.Exists(metaFilePath);
        }

        if (!audioExists || !metaExists)
        {
            Log.Debug("キャッシュミス: {CacheKey} - ファイルが存在しません", cacheKey);
            return null;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaFilePath);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

            if (metadata == null || this.IsExpired(metadata.CreatedAt))
            {
                Log.Debug("キャッシュミス: {CacheKey} - メタデータが無効または期限切れ", cacheKey);
                await this.DeleteCacheFileAsync(cacheKey);
                return null;
            }

            var audioData = await File.ReadAllBytesAsync(audioFilePath);

            // メモリキャッシュに保存（次回は高速アクセス）
            this._memoryCache.Set(cacheKey, audioData);

            Log.Debug("ディスクキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, audioData.Length);
            return audioData;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュファイルの読み込みに失敗: {CacheKey}", cacheKey);
            await this.DeleteCacheFileAsync(cacheKey);
            return null;
        }
    }

    public Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = this.ComputeCacheKey(request);
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

            // メモリキャッシュに保存（高速アクセス用）
            this._memoryCache.Set(cacheKey, mp3Data);

            Log.Debug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュの保存に失敗: {CacheKey}", cacheKey);
            throw new InvalidOperationException($"Failed to save audio cache: {ex.Message}", ex);
        }

                // After saving, enforce max size policy in background with proper cancellation
        _ = Task.Run(async () =>
        {
            try
            {
                // 軽量なキャンセレーション用トークン（短時間実行のため）
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await this.CleanupByMaxSizeAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("キャッシュクリーンアップがタイムアウトしました");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "バックグラウンドキャッシュクリーンアップに失敗しました");
            }
        }, CancellationToken.None);

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

    public async Task CleanupByMaxSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(this._settings.Directory))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(this._settings.Directory);
            var files = dirInfo.GetFiles("*.mp3", SearchOption.TopDirectoryOnly)
                .Select(f => new
                {
                    File = f,
                    Meta = new FileInfo(Path.Combine(this._settings.Directory, Path.GetFileNameWithoutExtension(f.Name) + ".meta.json"))
                })
                .ToList();

            long totalBytes = files.Sum(x => x.File.Length);
            long maxBytes = (long)(Math.Max(0.0, this._settings.MaxSizeGB) * 1024 * 1024 * 1024);

            if (maxBytes <= 0 || totalBytes <= maxBytes)
            {
                return;
            }

            // 並べ替え: メタの作成日時（なければファイルの最終更新）で古い順
            var ordered = files.OrderBy(x =>
            {
                try
                {
                    if (x.Meta.Exists)
                    {
                        var metaJson = File.ReadAllText(x.Meta.FullName);
                        var meta = JsonSerializer.Deserialize<CacheMetadata>(metaJson);
                        if (meta != null)
                        {
                            return meta.CreatedAt;
                        }
                    }
                }
                catch { }
                return x.File.LastWriteTimeUtc;
            }).ToList();

            foreach (var entry in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (totalBytes <= maxBytes)
                {
                    break;
                }

                try
                {
                    var cacheKey = Path.GetFileNameWithoutExtension(entry.File.Name);
                    await this.DeleteCacheFileAsync(cacheKey);
                    totalBytes -= entry.File.Length;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to cleanup by max size: {ex.Message}", ex);
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
    private static readonly SHA256 _sha256 = SHA256.Create();

    public string ComputeCacheKey(VoiceRequest request)
    {
        var keyString = String.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F2}|{3:F2}|{4:F2}", request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        // 軽量なハッシュ計算（SHA256の再利用）
        var hashBytes = _sha256.ComputeHash(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(this._settings.Directory))
        {
            Directory.CreateDirectory(this._settings.Directory);
        }
    }

    private void ResolveCacheBaseDirectory()
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
            // If resolution fails, keep original setting
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

            MediaFoundationManager.EnsureInitialized();
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

    public Task ClearAllCacheAsync()
    {
        try
        {
            // メモリキャッシュをクリア
            this._memoryCache.Clear();

            if (!Directory.Exists(this._settings.Directory))
            {
                return Task.CompletedTask;
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

            Log.Information("キャッシュをクリアしました - ディスク: {FileCount} ファイル, メモリ: クリア済み", audioFiles.Length + metaFiles.Length);
            // Filler cache cleanup moved to FillerManager to respect its settings
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュのクリアに失敗しました");
            throw new InvalidOperationException($"Failed to clear cache: {ex.Message}", ex);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        this._memoryCache.Dispose();
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
