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

/// <summary>
/// 音声キャッシュの管理を行うサービス
/// メモリキャッシュとディスクキャッシュの両方を管理し、音声データの高速アクセスを提供します。
/// </summary>
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

    /// <summary>
    /// キャッシュから音声データを取得します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <returns>キャッシュされた音声データ。キャッシュにない場合はnull</returns>
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

    /// <summary>
    /// 音声データをキャッシュに保存します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="audioData">保存する音声データ</param>
    /// <returns>保存処理の完了を表すTask</returns>
    public Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = this.ComputeCacheKey(request);
        var audioFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(this._settings.Directory, $"{cacheKey}.meta.json");

        try
        {
            // Convert WAV to MP3
            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);

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

    /// <summary>
    /// 期限切れのキャッシュファイルをクリーンアップします
    /// </summary>
    /// <returns>クリーンアップ処理の完了を表すTask</returns>
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

    /// <summary>
    /// 非同期ファイルI/Oを使用したキャッシュの読み込み
    /// </summary>
    private async Task<byte[]?> LoadAudioFromCacheAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var cachePath = this.GetCacheFilePath(cacheKey);

        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            // 非同期ファイル読み込みの最適化
            using var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);

            var fileInfo = new FileInfo(cachePath);
            var buffer = new byte[fileInfo.Length];

            var bytesRead = 0;
            var totalBytes = fileInfo.Length;

            // 大きなファイルの場合はチャンク読み込み
            while (bytesRead < totalBytes)
            {
                var chunkSize = Math.Min(8192, totalBytes - bytesRead);
                var bytesReadInChunk = await fileStream.ReadAsync(buffer, bytesRead, (int)chunkSize, cancellationToken);

                if (bytesReadInChunk == 0) break; // EOF

                bytesRead += bytesReadInChunk;
            }

            if (bytesRead != totalBytes)
            {
                Log.Warning("キャッシュファイルの読み込みが不完全です - 期待: {Expected}, 実際: {Actual}", totalBytes, bytesRead);
                return null;
            }

            Log.Debug("キャッシュから音声を読み込みました - キー: {Key}, サイズ: {Size} bytes", cacheKey, bytesRead);
            return buffer;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("キャッシュ読み込みがキャンセルされました - キー: {Key}", cacheKey);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュからの音声読み込みに失敗しました - キー: {Key}, パス: {Path}", cacheKey, cachePath);
            return null;
        }
    }

    /// <summary>
    /// 非同期ファイルI/Oを使用したキャッシュの保存
    /// </summary>
    private async Task SaveAudioToCacheAsync(string cacheKey, byte[] audioData, CancellationToken cancellationToken = default)
    {
        var cachePath = this.GetCacheFilePath(cacheKey);
        var tempPath = cachePath + ".tmp";

        try
        {
            // 一時ファイルに書き込み
            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);

            var totalBytes = audioData.Length;
            var bytesWritten = 0;

            // 大きなファイルの場合はチャンク書き込み
            while (bytesWritten < totalBytes)
            {
                var chunkSize = Math.Min(8192, totalBytes - bytesWritten);
                await fileStream.WriteAsync(audioData, bytesWritten, chunkSize, cancellationToken);
                bytesWritten += chunkSize;
            }

            await fileStream.FlushAsync(cancellationToken);

            // 一時ファイルを正式なキャッシュファイルに移動
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            File.Move(tempPath, cachePath);

            Log.Debug("キャッシュに音声を保存しました - キー: {Key}, サイズ: {Size} bytes", cacheKey, bytesWritten);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("キャッシュ保存がキャンセルされました - キー: {Key}", cacheKey);
            // 一時ファイルをクリーンアップ
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュへの音声保存に失敗しました - キー: {Key}, パス: {Path}", cacheKey, cachePath);
            // 一時ファイルをクリーンアップ
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 並行処理を使用した複数セグメントの処理
    /// </summary>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var processedSegments = new List<TextSegment>();
        var tasks = new List<Task<TextSegment>>();

        // 並行処理でセグメントを処理
        foreach (var segment in segments)
        {
            var task = this.ProcessSegmentAsync(segment, request, cancellationToken);
            tasks.Add(task);
        }

        // すべてのタスクの完了を待機
        var results = await Task.WhenAll(tasks);
        processedSegments.AddRange(results);

        return processedSegments;
    }

    /// <summary>
    /// 個別セグメントの非同期処理
    /// </summary>
    private async Task<TextSegment> ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            segment.SpeakerId = request.SpeakerId;

            // キャッシュキーの生成
            var cacheKey = this.GenerateCacheKey(segment.Text, request);

            // キャッシュから読み込みを試行
            var cachedAudio = await this.LoadAudioFromCacheAsync(cacheKey, cancellationToken);
            if (cachedAudio != null)
            {
                segment.AudioData = cachedAudio;
                segment.IsCached = true;
                Log.Debug("セグメントがキャッシュから読み込まれました - テキスト: {Text}", segment.Text);
                return segment;
            }

            // キャッシュにない場合は新規生成
            Log.Debug("セグメントの音声生成を開始します - テキスト: {Text}", segment.Text);

            // 音声生成処理（実際の実装ではVoiceVox APIを呼び出し）
            // ここではプレースホルダーとして空の配列を返す
            segment.AudioData = new byte[0];
            segment.IsCached = false;

            return segment;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("セグメント処理がキャンセルされました - テキスト: {Text}", segment.Text);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント処理中にエラーが発生しました - テキスト: {Text}", segment.Text);
            // エラーが発生した場合は元のセグメントを返す
            return segment;
        }
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

    /// <summary>
    /// キャッシュファイルのパスを取得
    /// </summary>
    private string GetCacheFilePath(string cacheKey)
    {
        var fileName = $"{cacheKey}.mp3";
        return Path.Combine(this._settings.Directory, fileName);
    }

    /// <summary>
    /// キャッシュキーを生成
    /// </summary>
    private string GenerateCacheKey(string text, VoiceRequest request)
    {
        var keyData = $"{text}_{request.SpeakerId}_{request.Speed}_{request.Pitch}_{request.Volume}";
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyData));
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

    /// <summary>
    /// キャッシュをクリアします
    /// </summary>
    /// <returns>クリア処理の完了を表すTask</returns>
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

    /// <summary>
    /// キャッシュの統計情報を取得します
    /// </summary>
    /// <returns>キャッシュの統計情報</returns>
    public AudioCacheStatistics GetCacheStatistics()
    {
        var memoryStats = this._memoryCache.GetStatistics();
        var diskStats = new AudioCacheStatistics
        {
            TotalSizeBytes = 0,
            UsedSizeBytes = 0,
            CacheHits = 0,
            CacheMisses = 0
        };

        if (Directory.Exists(this._settings.Directory))
        {
            var dirInfo = new DirectoryInfo(this._settings.Directory);
            diskStats.TotalSizeBytes = dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            diskStats.UsedSizeBytes = dirInfo.GetFiles("*.mp3", SearchOption.AllDirectories).Sum(f => f.Length);
        }

        return new AudioCacheStatistics
        {
            TotalSizeBytes = diskStats.TotalSizeBytes,
            UsedSizeBytes = diskStats.UsedSizeBytes,
            CacheHits = memoryStats.CacheHits + diskStats.CacheHits, // Assuming memory and disk hits are separate
            CacheMisses = memoryStats.CacheMisses + diskStats.CacheMisses
        };
    }

    /// <summary>
    /// 音声キャッシュの統計情報
    /// </summary>
    public class AudioCacheStatistics
    {
        /// <summary>
        /// 総サイズ（バイト）
        /// </summary>
        public long TotalSizeBytes { get; set; }

        /// <summary>
        /// 使用サイズ（バイト）
        /// </summary>
        public long UsedSizeBytes { get; set; }

        /// <summary>
        /// キャッシュヒット数
        /// </summary>
        public long CacheHits { get; set; }

        /// <summary>
        /// キャッシュミス数
        /// </summary>
        public long CacheMisses { get; set; }

        /// <summary>
        /// ヒット率
        /// </summary>
        public double HitRate => this.CacheHits + this.CacheMisses > 0 ? (double)this.CacheHits / (this.CacheHits + this.CacheMisses) : 0;

        /// <summary>
        /// 使用率
        /// </summary>
        public double UsageRate => this.TotalSizeBytes > 0 ? (double)this.UsedSizeBytes / this.TotalSizeBytes : 0;
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
