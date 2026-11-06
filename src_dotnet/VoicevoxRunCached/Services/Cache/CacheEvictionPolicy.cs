using Microsoft.Extensions.Logging;
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

    public CacheEvictionPolicy(DiskCacheStore disk, CacheSettings settings, ILogger logger, IClock clock)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// 最終利用から <c>ExpirationDays</c> を過ぎたキャッシュを削除する。
    /// </summary>
    /// <remarks>
    /// 最終利用日時は meta.json の更新日時 (保存時に CreatedAt を設定し、<see cref="DiskCacheStore.LoadAsync"/> が
    /// ヒットのたびに更新する)。新規保存のある実行ごとに全件走査するが、meta.json の中身は読まないので数万件でも stat のみで済む。
    /// 読み込み時も <see cref="DiskCacheStore.LoadAsync"/> が同じ基準で判定する。
    /// </remarks>
    public Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        CacheErrorMapper.WithMapping(
            "キャッシュクリーンアップ",
            null,
            _settings.Directory,
            _logger,
            () =>
            {
                if (!Directory.Exists(_settings.Directory))
                    return;

                var now = _clock.UtcNow;
                var expiredKeys = new List<string>();

                foreach (var metaFile in new DirectoryInfo(_settings.Directory).EnumerateFiles("*.meta.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (now - metaFile.LastWriteTimeUtc > _expiration)
                    {
                        expiredKeys.Add(DiskCacheStore.ExtractKeyFromMetadataPath(metaFile.FullName));
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
            });
        return Task.CompletedTask;
    }

    /// <summary>
    /// MaxSizeGb を超えていれば、最後に使われたのが古いものから削除する (LRU)。
    /// </summary>
    /// <remarks>
    /// 上限に達したキャッシュでは毎回ここを通るため、meta.json は読まず更新日時 (= 最終利用日時) で判定する
    /// (<see cref="CleanupExpiredAsync"/> と同じ理由)。よく使う定型文はヒットのたびに新しくなるので残る。
    /// </remarks>
    public Task CleanupByMaxSizeAsync(CancellationToken cancellationToken = default)
    {
        CacheErrorMapper.WithMapping(
            "サイズ制限によるキャッシュクリーンアップ",
            null,
            _settings.Directory,
            _logger,
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(_settings.Directory))
                    return;

                var entries = new DirectoryInfo(_settings.Directory).GetFiles($"*{DiskCacheStore.AudioExtension}", SearchOption.TopDirectoryOnly);

                long totalBytes = entries.Sum(x => x.Length);
                long maxBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);

                if (maxBytes <= 0 || totalBytes <= maxBytes)
                    return;

                var ordered = entries.OrderBy(ResolveTimestamp).ToList();

                int deleted = 0;
                foreach (var entry in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (totalBytes <= maxBytes)
                        break;

                    _disk.Delete(Path.GetFileNameWithoutExtension(entry.Name));
                    totalBytes -= entry.Length;
                    deleted++;
                }

                if (deleted > 0)
                {
                    _logger.LogInformation("サイズ制限によるキャッシュクリーンアップが完了しました - {Count} ファイル削除", deleted);
                }
            });
        return Task.CompletedTask;
    }

    /// <summary>
    /// エントリの古さ。meta.json があればその更新日時 (= 最終利用日時)、無ければ音声ファイルの更新日時。
    /// </summary>
    private static DateTime ResolveTimestamp(FileInfo audio)
    {
        var meta = new FileInfo(Path.ChangeExtension(audio.FullName, ".meta.json"));
        return meta.Exists ? meta.LastWriteTimeUtc : audio.LastWriteTimeUtc;
    }
}
