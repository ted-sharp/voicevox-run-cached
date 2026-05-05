using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// 期限切れキャッシュとサイズ上限を超えるキャッシュの eviction を担う。
/// </summary>
public class CacheEvictionPolicy
{
    private readonly DiskCacheStore _disk;
    private readonly CacheSettings _settings;
    private readonly TimeSpan _expiration;
    private readonly ILogger _logger;
    private readonly IClock _clock;

    public CacheEvictionPolicy(DiskCacheStore disk, CacheSettings settings, ILogger? logger = null, IClock? clock = null)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>
    /// 期限切れキャッシュを削除する。
    /// </summary>
    public Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        return CacheErrorMapper.WithMappingAsync<object?>(
            CacheErrorMapper.OperationKind.Cleanup,
            "キャッシュクリーンアップ",
            null,
            _settings.Directory,
            _logger,
            async () =>
            {
                if (!Directory.Exists(_settings.Directory))
                    return null;

                var metaFiles = _disk.GetMetadataFiles();
                var expiredKeys = new List<string>();

                foreach (var metaFile in metaFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var metadata = await _disk.ReadMetadataFromPathAsync(metaFile, cancellationToken);
                    if (metadata == null || _clock.UtcNow - metadata.CreatedAt > _expiration)
                    {
                        expiredKeys.Add(DiskCacheStore.ExtractKeyFromMetadataPath(metaFile));
                    }
                }

                foreach (var key in expiredKeys)
                {
                    _disk.Delete(key);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogInformation("期限切れキャッシュをクリーンアップしました - {Count} ファイル", expiredKeys.Count);
                }

                return null;
            });
    }

    /// <summary>
    /// MaxSizeGb を超えていれば古いものから削除する。
    /// </summary>
    public async Task CleanupByMaxSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(_settings.Directory))
                return;

            var dirInfo = new DirectoryInfo(_settings.Directory);
            var entries = dirInfo.GetFiles("*.mp3", SearchOption.TopDirectoryOnly)
                .Select(f => new FileEntry(
                    f,
                    new FileInfo(Path.Combine(_settings.Directory,
                        Path.GetFileNameWithoutExtension(f.Name) + ".meta.json"))))
                .ToList();

            long totalBytes = entries.Sum(x => x.File.Length);
            long maxBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);

            if (maxBytes <= 0 || totalBytes <= maxBytes)
                return;

            // タイムスタンプで降順比較するため、各エントリの timestamp を非同期で先に解決する
            var withTimestamps = new List<(FileEntry Entry, DateTime Timestamp)>(entries.Count);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ts = await ResolveTimestampAsync(entry, cancellationToken);
                withTimestamps.Add((entry, ts));
            }

            var ordered = withTimestamps.OrderBy(x => x.Timestamp).Select(x => x.Entry).ToList();

            int deleted = 0;
            foreach (var entry in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (totalBytes <= maxBytes)
                    break;

                try
                {
                    var key = Path.GetFileNameWithoutExtension(entry.File.Name);
                    _disk.Delete(key);
                    totalBytes -= entry.File.Length;
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "サイズクリーンアップ中のファイル削除に失敗: {File}", entry.File.Name);
                }
            }

            if (deleted > 0)
            {
                _logger.LogInformation("サイズ制限によるキャッシュクリーンアップが完了しました - {Count} ファイル削除", deleted);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("サイズクリーンアップがキャンセルされました");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "サイズクリーンアップに失敗しました");
        }
    }

    /// <summary>
    /// バックグラウンドでサイズ制限クリーンアップを実行する (fire-and-forget)。
    /// </summary>
    public Task RunBackgroundSizeCleanupAsync()
    {
        return Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMinutes(AudioConstants.CacheBackgroundEvictionTimeoutMinutes));
                await CleanupByMaxSizeAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("バックグラウンドキャッシュクリーンアップがタイムアウトしました");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "バックグラウンドキャッシュクリーンアップに失敗しました");
            }
        }, CancellationToken.None);
    }

    private async Task<DateTime> ResolveTimestampAsync(FileEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Meta.Exists)
        {
            var meta = await _disk.ReadMetadataFromPathAsync(entry.Meta.FullName, cancellationToken);
            if (meta != null)
                return meta.CreatedAt;
        }
        return entry.File.LastWriteTimeUtc;
    }

    private readonly record struct FileEntry(FileInfo File, FileInfo Meta);
}
