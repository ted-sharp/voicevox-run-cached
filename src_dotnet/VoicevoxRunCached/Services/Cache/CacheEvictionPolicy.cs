using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Exceptions;

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

    public CacheEvictionPolicy(DiskCacheStore disk, CacheSettings settings, ILogger? logger = null)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 期限切れキャッシュを削除する。
    /// </summary>
    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(_settings.Directory))
                return;

            var metaFiles = _disk.GetMetadataFiles();
            var expiredKeys = new List<string>();

            foreach (var metaFile in metaFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var metadata = _disk.ReadMetadata(metaFile);
                    if (metadata == null || DateTime.UtcNow - metadata.CreatedAt > _expiration)
                    {
                        expiredKeys.Add(DiskCacheStore.ExtractKeyFromMetadataPath(metaFile));
                    }
                }
                catch
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

            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("キャッシュクリーンアップがキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Cache cleanup was cancelled",
                "キャッシュクリーンアップがキャンセルされました。",
                null,
                "操作を再実行してください。"
            );
        }
        catch (VoicevoxRunCachedException)
        {
            // 既にドメイン例外に整形済みのものは二重 wrap せず素通し
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "キャッシュクリーンアップ中にアクセス権限エラーが発生しました");
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
            _logger.LogError(ex, "キャッシュクリーンアップ中にI/Oエラーが発生しました");
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
            _logger.LogError(ex, "キャッシュクリーンアップ中に予期しないエラーが発生しました");
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
    /// MaxSizeGb を超えていれば古いものから削除する。
    /// </summary>
    public void CleanupByMaxSize(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(_settings.Directory))
                return;

            var dirInfo = new DirectoryInfo(_settings.Directory);
            var entries = dirInfo.GetFiles("*.mp3", SearchOption.TopDirectoryOnly)
                .Select(f => new
                {
                    File = f,
                    Meta = new FileInfo(Path.Combine(_settings.Directory,
                        Path.GetFileNameWithoutExtension(f.Name) + ".meta.json"))
                })
                .ToList();

            long totalBytes = entries.Sum(x => x.File.Length);
            long maxBytes = (long)(Math.Max(0.0, _settings.MaxSizeGb) * 1024 * 1024 * 1024);

            if (maxBytes <= 0 || totalBytes <= maxBytes)
                return;

            var ordered = entries.OrderBy(x => GetFileTimestamp(x.File, x.Meta)).ToList();

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
        return Task.Run(() =>
        {
            try
            {
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromMinutes(AudioConstants.CacheBackgroundEvictionTimeoutMinutes));
                CleanupByMaxSize(cts.Token);
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

    private DateTime GetFileTimestamp(FileInfo file, FileInfo metaFile)
    {
        if (metaFile.Exists)
        {
            var meta = _disk.ReadMetadata(metaFile.FullName);
            if (meta != null)
                return meta.CreatedAt;
        }
        return file.LastWriteTimeUtc;
    }
}
