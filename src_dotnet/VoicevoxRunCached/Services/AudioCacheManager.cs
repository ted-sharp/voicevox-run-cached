using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声キャッシュの統合管理を行うサービス
/// メモリ・ディスクキャッシュとクリーンアップを統合制御し、音声データの高速アクセスを提供します。
/// </summary>
public class AudioCacheManager : IDisposable
{
    private static readonly SHA256 Sha256 = SHA256.Create();
    private readonly IMemoryCache _memoryCache;
    private readonly CacheSettings _settings;
    private readonly TimeSpan _cacheExpiration;
    private bool _disposed;

    // 統計情報
    private long _hitCount;
    private long _missCount;
    private int _memoryCacheEntries;

    public AudioCacheManager(CacheSettings settings, IMemoryCache? memoryCache = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _cacheExpiration = TimeSpan.FromDays(settings.ExpirationDays);

        var cacheOptions = new MemoryCacheOptions
        {
            SizeLimit = settings.MemoryCacheSizeMb * 1024L * 1024L
        };
        _memoryCache = memoryCache ?? new MemoryCache(cacheOptions);

        ResolveCacheBaseDirectory();
        EnsureCacheDirectoryExists();

        Log.Information("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効, 最大サイズ: {MaxSizeMB}MB",
            _settings.Directory, settings.MemoryCacheSizeMb);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    if (_memoryCache is MemoryCache mc)
                    {
                        mc.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "AudioCacheManagerの破棄中にエラーが発生しました");
                }
            }
            _disposed = true;
        }
    }

    private void EnsureCacheDirectoryExists()
    {
        if (!Directory.Exists(_settings.Directory))
        {
            Directory.CreateDirectory(_settings.Directory);
            Log.Debug("キャッシュディレクトリを作成しました: {Directory}", _settings.Directory);
        }
    }

    /// <summary>
    /// キャッシュから音声データを取得します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <returns>キャッシュされた音声データ。キャッシュにない場合はnull</returns>
    public async Task<byte[]?> GetCachedAudioAsync(VoiceRequest request)
    {
        var cacheKey = ComputeCacheKey(request);

        try
        {
            // まずメモリキャッシュをチェック
            if (_memoryCache.TryGetValue(cacheKey, out byte[]? memoryCached) && memoryCached != null)
            {
                Interlocked.Increment(ref _hitCount);
                Log.Debug("メモリキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, memoryCached.Length);
                return memoryCached;
            }

            // メモリキャッシュにない場合はディスクキャッシュをチェック
            var audioData = await LoadAudioFromDiskAsync(cacheKey);
            if (audioData != null)
            {
                Interlocked.Increment(ref _hitCount);
                // ディスクから読み込めた場合はメモリキャッシュに保存（次回は高速アクセス）
                SetMemoryCache(cacheKey, audioData);
                return audioData;
            }

            Interlocked.Increment(ref _missCount);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュファイルへのアクセス権限がありません: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied to cache file: {ex.Message}",
                "キャッシュファイルへのアクセス権限がありません。",
                cacheKey,
                _settings.Directory,
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュファイルの読み込みに失敗しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheReadError,
                $"Failed to read cache file: {ex.Message}",
                "キャッシュファイルの読み込みに失敗しました。",
                cacheKey,
                _settings.Directory,
                "キャッシュディレクトリの状態を確認し、必要に応じてキャッシュをクリアしてください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュからの音声データ取得中に予期しないエラーが発生しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error while retrieving cached audio: {ex.Message}",
                "キャッシュからの音声データ取得中に予期しないエラーが発生しました。",
                cacheKey,
                _settings.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    private void SetMemoryCache(string cacheKey, byte[] data)
    {
        var entryOptions = new MemoryCacheEntryOptions()
            .SetSize(data.Length)
            .SetAbsoluteExpiration(_cacheExpiration)
            .RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                Interlocked.Decrement(ref _memoryCacheEntries);
            });

        _memoryCache.Set(cacheKey, data, entryOptions);
        Interlocked.Increment(ref _memoryCacheEntries);
    }

    /// <summary>
    /// 音声データをキャッシュに保存します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="audioData">保存する音声データ</param>
    /// <returns>保存処理の完了を表すTask</returns>
    public async Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = ComputeCacheKey(request);

        try
        {
            // ディスクキャッシュに保存
            await SaveAudioToDiskAsync(request, audioData, cacheKey);

            // WAVをMP3に変換してメモリキャッシュに保存
            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            SetMemoryCache(cacheKey, mp3Data);

            Log.Debug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            // 保存後、バックグラウンドでサイズ制限ポリシーを適用
            _ = RunBackgroundCleanupAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュディレクトリへの書き込み権限がありません: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied to cache directory: {ex.Message}",
                "キャッシュディレクトリへの書き込み権限がありません。",
                cacheKey,
                _settings.Directory,
                "キャッシュディレクトリの書き込み権限を確認してください。"
            );
        }
        catch (IOException ex) when (ex.Message.Contains("There is not enough space"))
        {
            Log.Error(ex, "キャッシュディレクトリの容量が不足しています: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheFull,
                $"Cache directory is full: {ex.Message}",
                "キャッシュディレクトリの容量が不足しています。",
                cacheKey,
                _settings.Directory,
                "古いキャッシュファイルを削除するか、キャッシュディレクトリの容量を増やしてください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュファイルの書き込みに失敗しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheWriteError,
                $"Failed to write cache file: {ex.Message}",
                "キャッシュファイルの書き込みに失敗しました。",
                cacheKey,
                _settings.Directory,
                "ディスクの空き容量とキャッシュディレクトリの状態を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュの保存に失敗: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheWriteError,
                $"Failed to save audio cache: {ex.Message}",
                "キャッシュの保存に失敗しました。",
                cacheKey,
                _settings.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    /// <summary>
    /// 期限切れのキャッシュファイルをクリーンアップします
    /// </summary>
    /// <returns>クリーンアップ処理の完了を表すTask</returns>
    public async Task CleanupExpiredCacheAsync()
    {
        try
        {
            if (!Directory.Exists(_settings.Directory))
            {
                return;
            }

            var metaFiles = Directory.GetFiles(_settings.Directory, "*.meta.json");
            var expiredKeys = new List<string>();

            foreach (var metaFile in metaFiles)
            {
                try
                {
                    var metaJson = await File.ReadAllTextAsync(metaFile);
                    var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

                    if (metadata == null || DateTime.UtcNow - metadata.CreatedAt > _cacheExpiration)
                    {
                        var cacheKey = Path.GetFileNameWithoutExtension(metaFile).Replace(".meta", "");
                        expiredKeys.Add(cacheKey);
                    }
                }
                catch
                {
                    var cacheKey = Path.GetFileNameWithoutExtension(metaFile).Replace(".meta", "");
                    expiredKeys.Add(cacheKey);
                }
            }

            foreach (var cacheKey in expiredKeys)
            {
                DeleteCacheFile(cacheKey);
            }

            if (expiredKeys.Count > 0)
            {
                Log.Information("期限切れキャッシュをクリーンアップしました - {Count} ファイル", expiredKeys.Count);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("キャッシュクリーンアップがキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Cache cleanup was cancelled",
                "キャッシュクリーンアップがキャンセルされました。",
                null,
                "操作を再実行してください。"
            );
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュクリーンアップ中にアクセス権限エラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied during cache cleanup: {ex.Message}",
                "キャッシュクリーンアップ中にアクセス権限エラーが発生しました。",
                null,
                _settings.Directory,
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュクリーンアップ中にI/Oエラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CacheReadError,
                $"I/O error during cache cleanup: {ex.Message}",
                "キャッシュクリーンアップ中にI/Oエラーが発生しました。",
                null,
                _settings.Directory,
                "ディスクの状態とキャッシュディレクトリの権限を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュクリーンアップ中に予期しないエラーが発生しました");
            throw new CacheException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error during cache cleanup: {ex.Message}",
                "キャッシュクリーンアップ中に予期しないエラーが発生しました。",
                null,
                _settings.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    /// <summary>
    /// 最大サイズ制限によるキャッシュクリーンアップを実行します
    /// </summary>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    public void CleanupByMaxSize(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(_settings.Directory))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(_settings.Directory);
            var files = dirInfo.GetFiles("*.mp3", SearchOption.TopDirectoryOnly)
                .Select(f => new
                {
                    File = f,
                    Meta = new FileInfo(Path.Combine(_settings.Directory, Path.GetFileNameWithoutExtension(f.Name) + ".meta.json"))
                })
                .ToList();

            long totalBytes = files.Sum(x => x.File.Length);
            long maxBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);

            if (maxBytes <= 0 || totalBytes <= maxBytes)
            {
                return;
            }

            var ordered = files.OrderBy(x => GetFileTimestamp(x.File, x.Meta)).ToList();

            var deletedFiles = 0;
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
                    DeleteCacheFile(cacheKey);
                    totalBytes -= entry.File.Length;
                    deletedFiles++;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "サイズクリーンアップ中のファイル削除に失敗: {File}", entry.File.Name);
                }
            }

            if (deletedFiles > 0)
            {
                Log.Information("サイズ制限によるキャッシュクリーンアップが完了しました - {Count} ファイル削除", deletedFiles);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("サイズクリーンアップがキャンセルされました");
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "サイズクリーンアップに失敗しました");
        }
    }

    private static DateTime GetFileTimestamp(FileInfo file, FileInfo metaFile)
    {
        try
        {
            if (metaFile.Exists)
            {
                var metaJson = File.ReadAllText(metaFile.FullName);
                var meta = JsonSerializer.Deserialize<CacheMetadata>(metaJson);
                if (meta != null)
                {
                    return meta.CreatedAt;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read metadata file, using LastWriteTimeUtc instead");
        }
        return file.LastWriteTimeUtc;
    }

    private Task RunBackgroundCleanupAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                CleanupByMaxSize(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("バックグラウンドキャッシュクリーンアップがタイムアウトしました");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "バックグラウンドキャッシュクリーンアップに失敗しました");
            }
        }, CancellationToken.None);
    }


    /// <summary>
    /// 並行処理を使用した複数セグメントの処理
    /// </summary>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = segments.Select(segment => ProcessSegmentAsync(segment, request, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// 個別セグメントの非同期処理
    /// </summary>
    private async Task<TextSegment> ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            segment.SpeakerId = request.SpeakerId;

            // キャッシュから音声データを取得
            var segmentRequest = new VoiceRequest
            {
                Text = segment.Text,
                SpeakerId = request.SpeakerId,
                Speed = request.Speed,
                Pitch = request.Pitch,
                Volume = request.Volume
            };

            var cachedAudio = await GetCachedAudioAsync(segmentRequest);
            if (cachedAudio != null)
            {
                segment.AudioData = cachedAudio;
                segment.IsCached = true;
                Log.Debug("セグメントがキャッシュから読み込まれました - テキスト: {Text}", segment.Text);
            }
            else
            {
                // キャッシュにない場合は未キャッシュとしてマーク
                segment.AudioData = null;
                segment.IsCached = false;
                Log.Debug("セグメントはキャッシュにありません - テキスト: {Text}", segment.Text);
            }

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

    public static string ComputeCacheKey(VoiceRequest request)
    {
        var keyString = String.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F2}|{3:F2}|{4:F2}", request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        // 軽量なハッシュ計算（SHA256の再利用）
        var hashBytes = Sha256.ComputeHash(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }


    private void ResolveCacheBaseDirectory()
    {
        try
        {
            if (_settings.UseExecutableBaseDirectory && !Path.IsPathRooted(_settings.Directory))
            {
                var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
                var combined = Path.Combine(executableDirectory, _settings.Directory);
                _settings.Directory = Path.GetFullPath(combined);
            }
        }
        catch
        {
            // If resolution fails, keep original setting
        }
    }


    /// <summary>
    /// キャッシュをクリアします
    /// </summary>
    public void ClearAllCache()
    {
        try
        {
            // メモリキャッシュをクリア
            if (_memoryCache is MemoryCache mc)
            {
                mc.Compact(1.0);
            }
            _memoryCacheEntries = 0;

            // ディスクキャッシュをクリア
            var cacheFiles = GetCacheFiles();
            foreach (var file in cacheFiles)
            {
                var cacheKey = Path.GetFileNameWithoutExtension(file);
                DeleteCacheFile(cacheKey);
            }

            Log.Information("すべてのキャッシュをクリアしました - メモリ・ディスク共にクリア済み");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュのクリア中にアクセス権限エラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied while clearing cache: {ex.Message}",
                "キャッシュのクリア中にアクセス権限エラーが発生しました。",
                null,
                _settings.Directory,
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュのクリア中にI/Oエラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CacheReadError,
                $"I/O error while clearing cache: {ex.Message}",
                "キャッシュのクリア中にI/Oエラーが発生しました。",
                null,
                _settings.Directory,
                "ディスクの状態とキャッシュディレクトリの権限を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュのクリアに失敗しました");
            throw new CacheException(
                ErrorCodes.General.UnknownError,
                $"Failed to clear cache: {ex.Message}",
                "キャッシュのクリアに失敗しました。",
                null,
                _settings.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    /// <summary>
    /// キャッシュの統計情報を取得します
    /// </summary>
    /// <returns>キャッシュの統計情報</returns>
    public AudioCacheStatistics GetCacheStatistics()
    {
        try
        {
            var diskStats = GetDiskCacheStatistics();
            return new AudioCacheStatistics
            {
                TotalSizeBytes = diskStats.TotalSize,
                UsedSizeBytes = diskStats.TotalSize,
                CacheHits = Interlocked.Read(ref _hitCount),
                CacheMisses = Interlocked.Read(ref _missCount),
                DiskFileCount = diskStats.TotalFiles,
                MemoryCacheEntries = _memoryCacheEntries
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュ統計情報の取得に失敗しました");
            return new AudioCacheStatistics();
        }
    }

    /// <summary>
    /// 詳細なキャッシュ統計情報を取得します
    /// </summary>
    /// <returns>詳細統計情報</returns>
    public DetailedCacheStatistics GetDetailedStatistics()
    {
        try
        {
            var diskStats = GetDiskCacheStatistics();
            var maxSizeBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);
            var maxMemorySizeBytes = _settings.MemoryCacheSizeMb * 1024 * 1024;
            var hits = Interlocked.Read(ref _hitCount);
            var misses = Interlocked.Read(ref _missCount);

            return new DetailedCacheStatistics
            {
                DiskTotalSizeBytes = diskStats.TotalSize,
                DiskUsedSizeBytes = diskStats.TotalSize,
                DiskFileCount = diskStats.TotalFiles,
                DiskMaxSizeBytes = maxSizeBytes,
                DiskUsageRatio = maxSizeBytes > 0 ? (double)diskStats.TotalSize / maxSizeBytes : 0.0,
                MemoryCacheEntries = _memoryCacheEntries,
                MemoryMaxSizeBytes = maxMemorySizeBytes,
                MemoryCacheHits = hits,
                MemoryCacheMisses = misses,
                MemoryHitRatio = hits + misses > 0 ? (double)hits / (hits + misses) : 0.0,
                ExpirationDays = _settings.ExpirationDays,
                CacheDirectory = _settings.Directory
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "詳細統計情報の取得に失敗しました");
            return new DetailedCacheStatistics();
        }
    }

    /// <summary>
    /// キャッシュ効率の分析結果を取得します
    /// </summary>
    /// <returns>効率分析結果</returns>
    public CacheEfficiencyAnalysis AnalyzeCacheEfficiency()
    {
        try
        {
            var diskStats = GetDiskCacheStatistics();
            var maxSizeBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);
            var hits = Interlocked.Read(ref _hitCount);
            var misses = Interlocked.Read(ref _missCount);
            var totalAccesses = hits + misses;
            var hitRatio = totalAccesses > 0 ? (double)hits / totalAccesses : 0.0;

            var efficiency = CalculateEfficiencyScore(hitRatio, diskStats.TotalSize, maxSizeBytes, diskStats.TotalFiles);

            return new CacheEfficiencyAnalysis
            {
                OverallHitRatio = hitRatio,
                EfficiencyScore = efficiency,
                EfficiencyRating = GetEfficiencyRating(efficiency),
                SpaceUtilization = maxSizeBytes > 0 ? (double)diskStats.TotalSize / maxSizeBytes : 0.0,
                AverageFileSize = diskStats.TotalFiles > 0 ? diskStats.TotalSize / diskStats.TotalFiles : 0,
                Recommendations = GenerateRecommendations(hitRatio, diskStats.TotalSize, maxSizeBytes, diskStats.TotalFiles)
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュ効率分析に失敗しました");
            return new CacheEfficiencyAnalysis
            {
                OverallHitRatio = 0.0,
                EfficiencyScore = 0.0,
                EfficiencyRating = "Unknown",
                SpaceUtilization = 0.0,
                AverageFileSize = 0,
                Recommendations = new List<string> { "統計情報の取得に失敗しました。" }
            };
        }
    }

    private double CalculateEfficiencyScore(double hitRatio, long usedSize, long maxSize, int fileCount)
    {
        var hitScore = hitRatio * 40;
        var spaceScore = maxSize > 0 ? (1.0 - (double)usedSize / maxSize) * 30 : 30;
        var fileScore = fileCount > 0 ? Math.Min(30, fileCount / 10.0) : 0;
        return Math.Max(0, Math.Min(100, hitScore + spaceScore + fileScore));
    }

    private static string GetEfficiencyRating(double score)
    {
        return score switch
        {
            >= 90 => "Excellent",
            >= 80 => "Good",
            >= 70 => "Fair",
            >= 60 => "Poor",
            _ => "Very Poor"
        };
    }

    private static List<string> GenerateRecommendations(double hitRatio, long usedSize, long maxSize, int fileCount)
    {
        var recommendations = new List<string>();

        if (hitRatio < 0.5)
        {
            recommendations.Add("ヒット率が低いです。メモリキャッシュサイズの増加を検討してください。");
        }

        if (maxSize > 0 && (double)usedSize / maxSize > 0.9)
        {
            recommendations.Add("ディスクキャッシュの使用率が高いです。サイズ制限の増加またはクリーンアップ頻度の調整を検討してください。");
        }

        if (fileCount > 1000)
        {
            recommendations.Add("キャッシュファイル数が多いです。定期的なクリーンアップを実行してください。");
        }

        if (fileCount < 10)
        {
            recommendations.Add("キャッシュファイル数が少ないです。より多くのコンテンツをキャッシュすることで効率が向上する可能性があります。");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("キャッシュは効率的に動作しています。");
        }

        return recommendations;
    }

    #region Disk Cache Operations

    private async Task<byte[]?> LoadAudioFromDiskAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var audioFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.meta.json");

        if (!File.Exists(audioFilePath) || !File.Exists(metaFilePath))
        {
            Log.Debug("キャッシュミス: {CacheKey} - ファイルが存在しません", cacheKey);
            return null;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaFilePath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

            if (metadata == null || !IsMetadataValid(metadata))
            {
                Log.Debug("キャッシュミス: {CacheKey} - メタデータが無効", cacheKey);
                DeleteCacheFile(cacheKey);
                return null;
            }

            if (DateTime.UtcNow - metadata.CreatedAt > _cacheExpiration)
            {
                Log.Debug("キャッシュミス: {CacheKey} - メタデータが期限切れ", cacheKey);
                DeleteCacheFile(cacheKey);
                return null;
            }

            var audioData = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
            Log.Debug("ディスクキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, audioData.Length);
            return audioData;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュファイルの読み込みに失敗: {CacheKey}", cacheKey);
            DeleteCacheFile(cacheKey);
            return null;
        }
    }

    private async Task SaveAudioToDiskAsync(VoiceRequest request, byte[] audioData, string cacheKey)
    {
        var audioFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
        var metaFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.meta.json");

        var metadata = new CacheMetadata
        {
            CreatedAt = DateTime.UtcNow,
            Text = request.Text,
            SpeakerId = request.SpeakerId,
            Speed = request.Speed,
            Pitch = request.Pitch,
            Volume = request.Volume
        };

        var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
        await File.WriteAllBytesAsync(audioFilePath, mp3Data);

        var metaJson = JsonSerializer.Serialize(metadata, JsonSerializerOptionsCache.Indented);
        await File.WriteAllTextAsync(metaFilePath, metaJson);

        Log.Debug("キャッシュファイルを保存しました: {CacheKey}, サイズ: {Size} bytes", cacheKey, mp3Data.Length);
    }

    private void DeleteCacheFile(string cacheKey)
    {
        try
        {
            var audioFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
            var metaFilePath = Path.Combine(_settings.Directory, $"{cacheKey}.meta.json");

            if (File.Exists(audioFilePath))
                File.Delete(audioFilePath);
            if (File.Exists(metaFilePath))
                File.Delete(metaFilePath);

            Log.Debug("キャッシュファイルを削除しました: {CacheKey}", cacheKey);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュファイルの削除に失敗: {CacheKey}", cacheKey);
        }
    }

    private string[] GetCacheFiles()
    {
        try
        {
            return Directory.GetFiles(_settings.Directory, "*.mp3");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ファイル一覧の取得に失敗: {Directory}", _settings.Directory);
            return Array.Empty<string>();
        }
    }

    private (int TotalFiles, long TotalSize) GetDiskCacheStatistics()
    {
        try
        {
            var cacheFiles = GetCacheFiles();
            var totalSize = 0L;
            var validFiles = 0;

            foreach (var filePath in cacheFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    totalSize += fileInfo.Length;
                    validFiles++;
                }
                catch { }
            }

            return (validFiles, totalSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "キャッシュ統計情報の取得に失敗しました");
            return (0, 0);
        }
    }

    private static bool IsMetadataValid(CacheMetadata metadata)
    {
        return !String.IsNullOrWhiteSpace(metadata.Text) &&
               metadata.SpeakerId > 0 &&
               metadata.CreatedAt != default;
    }

    #endregion
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
    /// ディスクファイル数
    /// </summary>
    public int DiskFileCount { get; set; }

    /// <summary>
    /// メモリキャッシュエントリ数
    /// </summary>
    public int MemoryCacheEntries { get; set; }

    /// <summary>
    /// ヒット率
    /// </summary>
    public double HitRate => CacheHits + CacheMisses > 0 ? (double)CacheHits / (CacheHits + CacheMisses) : 0;

    /// <summary>
    /// 使用率
    /// </summary>
    public double UsageRate => TotalSizeBytes > 0 ? (double)UsedSizeBytes / TotalSizeBytes : 0;
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

/// <summary>
/// 詳細なキャッシュ統計情報
/// </summary>
public class DetailedCacheStatistics
{
    public long DiskTotalSizeBytes { get; set; }
    public long DiskUsedSizeBytes { get; set; }
    public int DiskFileCount { get; set; }
    public long DiskMaxSizeBytes { get; set; }
    public double DiskUsageRatio { get; set; }

    public int MemoryCacheEntries { get; set; }
    public long MemoryMaxSizeBytes { get; set; }
    public long MemoryCacheHits { get; set; }
    public long MemoryCacheMisses { get; set; }
    public double MemoryHitRatio { get; set; }

    public int ExpirationDays { get; set; }
    public string CacheDirectory { get; set; } = String.Empty;
}

/// <summary>
/// キャッシュ効率性の分析結果
/// </summary>
public class CacheEfficiencyAnalysis
{
    public double OverallHitRatio { get; set; }
    public double EfficiencyScore { get; set; }
    public string EfficiencyRating { get; set; } = String.Empty;
    public double SpaceUtilization { get; set; }
    public long AverageFileSize { get; set; }
    public List<string> Recommendations { get; set; } = new();
}


