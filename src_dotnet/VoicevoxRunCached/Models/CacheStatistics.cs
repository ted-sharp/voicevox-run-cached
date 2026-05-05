namespace VoicevoxRunCached.Models;

/// <summary>
/// 音声キャッシュの統計情報
/// </summary>
public class AudioCacheStatistics
{
    public long TotalSizeBytes { get; set; }
    public long UsedSizeBytes { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public int DiskFileCount { get; set; }
    public int MemoryCacheEntries { get; set; }

    public double HitRate => CacheHits + CacheMisses > 0 ? (double)CacheHits / (CacheHits + CacheMisses) : 0;
    public double UsageRate => TotalSizeBytes > 0 ? (double)UsedSizeBytes / TotalSizeBytes : 0;
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
