using System.Text.Json;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// ディスク上の Ogg Opus + .meta.json ペアの読み書き・削除を提供する。
/// </summary>
public class DiskCacheStore
{
    /// <summary>キャッシュ音声の拡張子。</summary>
    public const string AudioExtension = ".opus";

    /// <summary>v2 以前の MP3 キャッシュの拡張子。読み込まず、掃除時に削除する。</summary>
    public const string LegacyAudioExtension = ".mp3";

    private readonly CacheSettings _settings;
    private readonly TimeSpan _expiration;
    private readonly ILogger _logger;
    private readonly IClock _clock;

    public DiskCacheStore(CacheSettings settings, ILogger logger, IClock clock)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // パスの正規化は ApplicationBootstrap.BuildAppSettings 側で完了済み
        EnsureDirectoryExists();
    }

    public string Directory => _settings.Directory;

    /// <summary>
    /// ディスクキャッシュから音声データを読み込む。期限切れ・メタデータ不整合の場合は削除して null を返す。
    /// ヒットしたら最終利用日時を今に更新する。
    /// </summary>
    /// <remarks>
    /// 最終利用日時は meta.json の更新日時で表す (保存時に CreatedAt を設定し、ヒットのたびに更新する)。
    /// 期限 (<c>ExpirationDays</c>) はここから数え、容量超過時の削除 (<see cref="CacheEvictionPolicy"/>) も古い順に使う。
    /// 作成日時で数えると、よく使う定型文ほど定期的に消えて作り直しになるため。
    /// </remarks>
    public async Task<byte[]?> LoadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var (audioPath, metaPath) = ResolvePaths(cacheKey);

        if (!File.Exists(audioPath) || !File.Exists(metaPath))
        {
            _logger.LogDebug("キャッシュミス: {CacheKey} - ファイルが存在しません", cacheKey);
            return null;
        }

        try
        {
            var metaJson = await File.ReadAllTextAsync(metaPath, cancellationToken);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metaJson);

            if (metadata == null || !IsMetadataValid(metadata))
            {
                _logger.LogDebug("キャッシュミス: {CacheKey} - メタデータが無効", cacheKey);
                Delete(cacheKey);
                return null;
            }

            var lastUsedAt = File.GetLastWriteTimeUtc(metaPath);
            if (_clock.UtcNow - lastUsedAt > _expiration)
            {
                _logger.LogDebug("キャッシュミス: {CacheKey} - 期限切れ (最終利用: {LastUsedAt:O})", cacheKey, lastUsedAt);
                Delete(cacheKey);
                return null;
            }

            var audioData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            MarkUsed(cacheKey, metaPath);
            _logger.LogDebug("ディスクキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, audioData.Length);
            return audioData;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C で読み込みが中断されただけなので、壊れたファイル扱いで削除しない
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュファイルの読み込みに失敗: {CacheKey}", cacheKey);
            Delete(cacheKey);
            return null;
        }
    }

    /// <summary>
    /// 音声データ (Ogg Opus) とメタデータをディスクに保存する。
    /// </summary>
    public async Task SaveAsync(VoiceRequest request, byte[] audioData, string cacheKey, CancellationToken cancellationToken = default)
    {
        var (audioPath, metaPath) = ResolvePaths(cacheKey);

        var metadata = new CacheMetadata
        {
            CreatedAt = _clock.UtcNow,
            Text = request.Text,
            SpeakerId = request.SpeakerId,
            Speed = request.Speed,
            Pitch = request.Pitch,
            Volume = request.Volume
        };

        await File.WriteAllBytesAsync(audioPath, audioData, cancellationToken);

        var metaJson = JsonSerializer.Serialize(metadata, JsonSerializerOptionsCache.Indented);
        await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken);
        // 最終利用日時 (meta.json の更新日時) を IClock の時刻に揃える (期限・eviction の判定は IClock と比べるため)
        File.SetLastWriteTimeUtc(metaPath, metadata.CreatedAt);

        _logger.LogDebug("キャッシュファイルを保存しました: {CacheKey}, サイズ: {Size} bytes", cacheKey, audioData.Length);
    }

    /// <summary>
    /// 最終利用日時 (meta.json の更新日時) を今にする。失敗しても再生には影響しないので Warning で続行する。
    /// </summary>
    private void MarkUsed(string cacheKey, string metaPath)
    {
        try
        {
            File.SetLastWriteTimeUtc(metaPath, _clock.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "キャッシュの最終利用日時を更新できませんでした: {CacheKey}", cacheKey);
        }
    }

    /// <summary>
    /// 音声と meta.json のペアを削除。
    /// </summary>
    public void Delete(string cacheKey)
    {
        try
        {
            var (audioPath, metaPath) = ResolvePaths(cacheKey);

            if (File.Exists(audioPath))
                File.Delete(audioPath);
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            _logger.LogDebug("キャッシュファイルを削除しました: {CacheKey}", cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュファイルの削除に失敗: {CacheKey}", cacheKey);
        }
    }

    /// <summary>
    /// --clear 用。キャッシュディレクトリ内の音声 (Opus / 旧 MP3) と meta.json をすべて削除する。
    /// </summary>
    /// <remarks>
    /// best-effort の <see cref="Delete"/> と違い失敗を握りつぶさない。呼び出し元
    /// (<see cref="AudioCacheManager.ClearAllCacheAsync"/>) が CacheErrorMapper で CacheException に変換する。
    /// </remarks>
    /// <returns>削除したファイル数</returns>
    public int DeleteAll()
    {
        if (!System.IO.Directory.Exists(_settings.Directory))
            return 0;

        string[] patterns = [$"*{AudioExtension}", "*.meta.json", $"*{LegacyAudioExtension}"];
        int deleted = 0;
        foreach (var pattern in patterns)
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(_settings.Directory, pattern))
            {
                File.Delete(path);
                deleted++;
            }
        }
        return deleted;
    }

    /// <summary>
    /// v2 以前の MP3 キャッシュを削除する。meta.json は同じキーの Opus が無い場合のみ削除する
    /// (キーは共通なので、再合成済みなら meta.json は Opus 側のもの)。
    /// </summary>
    /// <returns>削除した MP3 の件数</returns>
    public int DeleteLegacyFiles()
    {
        if (!System.IO.Directory.Exists(_settings.Directory))
            return 0;

        int deleted = 0;
        foreach (var legacyPath in System.IO.Directory.EnumerateFiles(_settings.Directory, $"*{LegacyAudioExtension}"))
        {
            var cacheKey = Path.GetFileNameWithoutExtension(legacyPath);
            var (audioPath, metaPath) = ResolvePaths(cacheKey);
            try
            {
                File.Delete(legacyPath);
                if (!File.Exists(audioPath) && File.Exists(metaPath))
                    File.Delete(metaPath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "旧形式キャッシュの削除に失敗: {Path}", legacyPath);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("旧形式 (MP3) のキャッシュを {Count} 件削除しました", deleted);
        }
        return deleted;
    }

    /// <summary>
    /// メタデータからキャッシュキーを推定 (ファイル名先頭部分)。
    /// </summary>
    public static string ExtractKeyFromMetadataPath(string metaFilePath)
    {
        return Path.GetFileNameWithoutExtension(metaFilePath).Replace(".meta", "");
    }

    /// <summary>
    /// メタデータの整合性検証 (Text が非空、SpeakerId >= 0、CreatedAt が default でない)。
    /// VOICEVOX は speaker 0 (四国めたん あまあま等) を持つため 0 も有効。
    /// </summary>
    /// <remarks>
    /// Speed/Pitch/Volume 未反映だった旧版のキャッシュは全て MP3 なので、Opus 移行で読まれなくなった (検証不要)。
    /// </remarks>
    public static bool IsMetadataValid(CacheMetadata metadata)
    {
        return !String.IsNullOrWhiteSpace(metadata.Text)
            && metadata.SpeakerId >= 0
            && metadata.CreatedAt != default;
    }

    private (string AudioPath, string MetaPath) ResolvePaths(string cacheKey)
    {
        var audio = Path.Combine(_settings.Directory, $"{cacheKey}{AudioExtension}");
        var meta = Path.Combine(_settings.Directory, $"{cacheKey}.meta.json");
        return (audio, meta);
    }

    private void EnsureDirectoryExists()
    {
        if (!System.IO.Directory.Exists(_settings.Directory))
        {
            System.IO.Directory.CreateDirectory(_settings.Directory);
            _logger.LogDebug("キャッシュディレクトリを作成しました: {Directory}", _settings.Directory);
        }
    }
}
