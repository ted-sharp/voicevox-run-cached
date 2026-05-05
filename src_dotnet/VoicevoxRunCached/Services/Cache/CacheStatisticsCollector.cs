using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// メモリ・ディスク両方の統計値を集計し、効率分析と推奨事項を生成する。
/// </summary>
public class CacheStatisticsCollector
{
    private readonly MemoryCacheStore _memory;
    private readonly DiskCacheStore _disk;
    private readonly CacheSettings _settings;
    private readonly ILogger _logger;

    public CacheStatisticsCollector(MemoryCacheStore memory, DiskCacheStore disk, CacheSettings settings, ILogger? logger = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullLogger.Instance;
    }

    public AudioCacheStatistics GetSummary()
    {
        try
        {
            var (totalFiles, totalSize) = _disk.GetStatistics();
            return new AudioCacheStatistics
            {
                TotalSizeBytes = totalSize,
                UsedSizeBytes = totalSize,
                CacheHits = _memory.HitCount,
                CacheMisses = _memory.MissCount,
                DiskFileCount = totalFiles,
                MemoryCacheEntries = _memory.EntryCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュ統計情報の取得に失敗しました");
            return new AudioCacheStatistics();
        }
    }

    public DetailedCacheStatistics GetDetailed()
    {
        try
        {
            var (totalFiles, totalSize) = _disk.GetStatistics();
            var maxSizeBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);
            var maxMemoryBytes = (long)_settings.MemoryCacheSizeMb * 1024 * 1024;
            var hits = _memory.HitCount;
            var misses = _memory.MissCount;

            return new DetailedCacheStatistics
            {
                DiskTotalSizeBytes = totalSize,
                DiskUsedSizeBytes = totalSize,
                DiskFileCount = totalFiles,
                DiskMaxSizeBytes = maxSizeBytes,
                DiskUsageRatio = maxSizeBytes > 0 ? (double)totalSize / maxSizeBytes : 0.0,
                MemoryCacheEntries = _memory.EntryCount,
                MemoryMaxSizeBytes = maxMemoryBytes,
                MemoryCacheHits = hits,
                MemoryCacheMisses = misses,
                MemoryHitRatio = hits + misses > 0 ? (double)hits / (hits + misses) : 0.0,
                ExpirationDays = _settings.ExpirationDays,
                CacheDirectory = _settings.Directory
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "詳細統計情報の取得に失敗しました");
            return new DetailedCacheStatistics();
        }
    }

    public CacheEfficiencyAnalysis Analyze()
    {
        try
        {
            var (totalFiles, totalSize) = _disk.GetStatistics();
            var maxSizeBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);
            var hits = _memory.HitCount;
            var misses = _memory.MissCount;
            var totalAccesses = hits + misses;
            var hitRatio = totalAccesses > 0 ? (double)hits / totalAccesses : 0.0;

            var efficiency = CalculateEfficiencyScore(hitRatio, totalSize, maxSizeBytes, totalFiles);

            return new CacheEfficiencyAnalysis
            {
                OverallHitRatio = hitRatio,
                EfficiencyScore = efficiency,
                EfficiencyRating = GetEfficiencyRating(efficiency),
                SpaceUtilization = maxSizeBytes > 0 ? (double)totalSize / maxSizeBytes : 0.0,
                AverageFileSize = totalFiles > 0 ? totalSize / totalFiles : 0,
                Recommendations = GenerateRecommendations(hitRatio, totalSize, maxSizeBytes, totalFiles)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュ効率分析に失敗しました");
            return new CacheEfficiencyAnalysis
            {
                EfficiencyRating = "Unknown",
                Recommendations = new List<string> { "統計情報の取得に失敗しました。" }
            };
        }
    }

    private static double CalculateEfficiencyScore(double hitRatio, long usedSize, long maxSize, int fileCount)
    {
        var hitScore = hitRatio * 40;
        var spaceScore = maxSize > 0 ? (1.0 - (double)usedSize / maxSize) * 30 : 30;
        var fileScore = fileCount > 0 ? Math.Min(30, fileCount / 10.0) : 0;
        return Math.Max(0, Math.Min(100, hitScore + spaceScore + fileScore));
    }

    private static string GetEfficiencyRating(double score) => score switch
    {
        >= 90 => "Excellent",
        >= 80 => "Good",
        >= 70 => "Fair",
        >= 60 => "Poor",
        _ => "Very Poor"
    };

    private static List<string> GenerateRecommendations(double hitRatio, long usedSize, long maxSize, int fileCount)
    {
        var list = new List<string>();

        if (hitRatio < 0.5)
        {
            list.Add("ヒット率が低いです。メモリキャッシュサイズの増加を検討してください。");
        }

        if (maxSize > 0 && (double)usedSize / maxSize > 0.9)
        {
            list.Add("ディスクキャッシュの使用率が高いです。サイズ制限の増加またはクリーンアップ頻度の調整を検討してください。");
        }

        if (fileCount > 1000)
        {
            list.Add("キャッシュファイル数が多いです。定期的なクリーンアップを実行してください。");
        }

        if (fileCount < 10)
        {
            list.Add("キャッシュファイル数が少ないです。より多くのコンテンツをキャッシュすることで効率が向上する可能性があります。");
        }

        if (list.Count == 0)
        {
            list.Add("キャッシュは効率的に動作しています。");
        }

        return list;
    }
}
