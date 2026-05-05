using Serilog;
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

    public CacheEvictionPolicy(DiskCacheStore disk, CacheSettings settings)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
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
                Log.Information("期限切れキャッシュをクリーンアップしました - {Count} ファイル", expiredKeys.Count);
            }

            await Task.CompletedTask;
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
                    Log.Warning(ex, "サイズクリーンアップ中のファイル削除に失敗: {File}", entry.File.Name);
                }
            }

            if (deleted > 0)
            {
                Log.Information("サイズ制限によるキャッシュクリーンアップが完了しました - {Count} ファイル削除", deleted);
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
                Log.Debug("バックグラウンドキャッシュクリーンアップがタイムアウトしました");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "バックグラウンドキャッシュクリーンアップに失敗しました");
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
