using System.Security.Cryptography;
using System.Text;
using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声キャッシュの統合管理を行うサービス
/// 各専門クラスを統合制御し、音声データの高速アクセスを提供します。
/// </summary>
public class AudioCacheManager : IDisposable
{
    // C# 13 ref readonly parameter for better performance with large structs
    private static readonly SHA256 Sha256 = SHA256.Create();
    private readonly CacheCleanupService _cleanupService;
    private readonly DiskCacheService _diskCacheService;
    private readonly MemoryCacheService _memoryCache;
    private readonly CacheSettings _settings;
    private readonly CacheStatisticsService _statisticsService;
    private bool _disposed;

    public AudioCacheManager(CacheSettings settings, MemoryCacheService? memoryCache = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _memoryCache = memoryCache ?? new MemoryCacheService(settings);

        ResolveCacheBaseDirectory();

        // 各専門クラスのインスタンスを作成
        _diskCacheService = new DiskCacheService(settings);
        _cleanupService = new CacheCleanupService(settings, _diskCacheService);
        _statisticsService = new CacheStatisticsService(settings, _diskCacheService, _memoryCache);

        Log.Information("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効", _settings.Directory);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _memoryCache.Dispose();
                _disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AudioCacheManagerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
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
            var memoryCached = _memoryCache.Get<byte[]>(cacheKey);
            if (memoryCached != null)
            {
                Log.Debug("メモリキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, memoryCached.Length);
                return memoryCached;
            }

            // メモリキャッシュにない場合はディスクキャッシュをチェック
            var audioData = await _diskCacheService.LoadAudioFromDiskAsync(cacheKey);
            if (audioData != null)
            {
                // ディスクから読み込めた場合はメモリキャッシュに保存（次回は高速アクセス）
                _memoryCache.Set(cacheKey, audioData);
            }

            return audioData;
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
            await _diskCacheService.SaveAudioToDiskAsync(request, audioData, cacheKey);

            // WAVをMP3に変換してメモリキャッシュに保存
            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            _memoryCache.Set(cacheKey, mp3Data);

            Log.Debug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            // 保存後、バックグラウンドでサイズ制限ポリシーを適用
            _ = _cleanupService.RunBackgroundCleanupAsync();
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
            await _cleanupService.CleanupExpiredCacheAsync();
            Log.Information("期限切れキャッシュのクリーンアップが完了しました");
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
        _cleanupService.CleanupByMaxSize(cancellationToken);
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
    private async Task<TextSegment> ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken _)
    {
        try
        {
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

    public string ComputeCacheKey(VoiceRequest request)
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
            _memoryCache.Clear();

            // ディスクキャッシュをクリア
            // ディスクキャッシュのクリア処理を実装
            var cacheFiles = _diskCacheService.GetCacheFiles();
            foreach (var file in cacheFiles)
            {
                var cacheKey = Path.GetFileNameWithoutExtension(file);
                _diskCacheService.DeleteCacheFile(cacheKey);
            }

            Log.Information("すべてのキャッシュをクリアしました - メモリ・ディスク共にクリア済み");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュのクリアに失敗しました");
            throw new InvalidOperationException($"Failed to clear cache: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// キャッシュの統計情報を取得します
    /// </summary>
    /// <returns>キャッシュの統計情報</returns>
    public AudioCacheStatistics GetCacheStatistics()
    {
        return _statisticsService.GetCombinedCacheStatistics();
    }

    /// <summary>
    /// 詳細なキャッシュ統計情報を取得します
    /// </summary>
    /// <returns>詳細統計情報</returns>
    public DetailedCacheStatistics GetDetailedStatistics()
    {
        return _statisticsService.GetDetailedStatistics();
    }

    /// <summary>
    /// キャッシュ効率の分析結果を取得します
    /// </summary>
    /// <returns>効率分析結果</returns>
    public CacheEfficiencyAnalysis AnalyzeCacheEfficiency()
    {
        return _statisticsService.AnalyzeCacheEfficiency();
    }
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


