using System.Security.Cryptography;
using System.Text;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声キャッシュの統合管理を行うサービス
/// 各専門クラスを統合制御し、音声データの高速アクセスを提供します。
/// </summary>
public class AudioCacheManager : IDisposable
{
    private readonly CacheSettings _settings;
    private readonly MemoryCacheService _memoryCache;
    private readonly DiskCacheService _diskCacheService;
    private readonly CacheCleanupService _cleanupService;
    private readonly CacheStatisticsService _statisticsService;
    private bool _disposed;

    public AudioCacheManager(CacheSettings settings, MemoryCacheService? memoryCache = null)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._memoryCache = memoryCache ?? new MemoryCacheService(settings);

        this.ResolveCacheBaseDirectory();

        // 各専門クラスのインスタンスを作成
        this._diskCacheService = new DiskCacheService(settings);
        this._cleanupService = new CacheCleanupService(settings, this._diskCacheService);
        this._statisticsService = new CacheStatisticsService(settings, this._diskCacheService, this._memoryCache);

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
        var audioData = await this._diskCacheService.LoadAudioFromDiskAsync(cacheKey);
        if (audioData != null)
        {
            // ディスクから読み込めた場合はメモリキャッシュに保存（次回は高速アクセス）
            this._memoryCache.Set(cacheKey, audioData);
        }

        return audioData;
    }

    /// <summary>
    /// 音声データをキャッシュに保存します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="audioData">保存する音声データ</param>
    /// <returns>保存処理の完了を表すTask</returns>
    public async Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = this.ComputeCacheKey(request);

        try
        {
            // ディスクキャッシュに保存
            await this._diskCacheService.SaveAudioToDiskAsync(request, audioData, cacheKey);

            // WAVをMP3に変換してメモリキャッシュに保存
            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            this._memoryCache.Set(cacheKey, mp3Data);

            Log.Debug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            // 保存後、バックグラウンドでサイズ制限ポリシーを適用
            _ = this._cleanupService.RunBackgroundCleanupAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュの保存に失敗: {CacheKey}", cacheKey);
            throw new InvalidOperationException($"Failed to save audio cache: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 期限切れのキャッシュファイルをクリーンアップします
    /// </summary>
    /// <returns>クリーンアップ処理の完了を表すTask</returns>
    public async Task CleanupExpiredCacheAsync()
    {
        await this._cleanupService.CleanupExpiredCacheAsync();
    }

    /// <summary>
    /// 最大サイズ制限によるキャッシュクリーンアップを実行します
    /// </summary>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>クリーンアップ処理の完了を表すTask</returns>
    public async Task CleanupByMaxSizeAsync(CancellationToken cancellationToken = default)
    {
        await this._cleanupService.CleanupByMaxSizeAsync(cancellationToken);
    }



    /// <summary>
    /// 並行処理を使用した複数セグメントの処理
    /// </summary>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = segments.Select(segment => this.ProcessSegmentAsync(segment, request, cancellationToken));
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

            var cachedAudio = await this.GetCachedAudioAsync(segmentRequest);
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



    /// <summary>
    /// キャッシュをクリアします
    /// </summary>
    /// <returns>クリア処理の完了を表すTask</returns>
    public async Task ClearAllCacheAsync()
    {
        try
        {
            // メモリキャッシュをクリア
            this._memoryCache.Clear();

            // ディスクキャッシュをクリア
            await this._diskCacheService.ClearAllCacheFilesAsync();

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
        return this._statisticsService.GetCombinedCacheStatistics();
    }

    /// <summary>
    /// 詳細なキャッシュ統計情報を取得します
    /// </summary>
    /// <returns>詳細統計情報</returns>
    public DetailedCacheStatistics GetDetailedStatistics()
    {
        return this._statisticsService.GetDetailedStatistics();
    }

    /// <summary>
    /// キャッシュ効率の分析結果を取得します
    /// </summary>
    /// <returns>効率分析結果</returns>
    public CacheEfficiencyAnalysis AnalyzeCacheEfficiency()
    {
        return this._statisticsService.AnalyzeCacheEfficiency();
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this._memoryCache?.Dispose();
                this._disposed = true;
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
    public double HitRate => this.CacheHits + this.CacheMisses > 0 ? (double)this.CacheHits / (this.CacheHits + this.CacheMisses) : 0;

    /// <summary>
    /// 使用率
    /// </summary>
    public double UsageRate => this.TotalSizeBytes > 0 ? (double)this.UsedSizeBytes / this.TotalSizeBytes : 0;
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


