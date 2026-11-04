using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// ディスク上の MP3 + .meta.json ペアの読み書き・削除・統計を提供する。
/// </summary>
public class DiskCacheStore
{
    private readonly CacheSettings _settings;
    private readonly TimeSpan _expiration;
    private readonly ILogger _logger;
    private readonly IClock _clock;

    public DiskCacheStore(CacheSettings settings, ILogger? logger = null, IClock? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? NullLogger.Instance;
        _clock = clock ?? SystemClock.Instance;
        // パスの正規化は ApplicationBootstrap.BuildAppSettings 側で完了済み
        EnsureDirectoryExists();
    }

    public string Directory => _settings.Directory;

    /// <summary>
    /// ディスクキャッシュから音声データを読み込む。期限切れ・メタデータ不整合の場合は削除して null を返す。
    /// </summary>
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

            if (_clock.UtcNow - metadata.CreatedAt > _expiration)
            {
                _logger.LogDebug("キャッシュミス: {CacheKey} - メタデータが期限切れ", cacheKey);
                Delete(cacheKey);
                return null;
            }

            var audioData = await File.ReadAllBytesAsync(audioPath, cancellationToken);
            _logger.LogDebug("ディスクキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, audioData.Length);
            return audioData;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュファイルの読み込みに失敗: {CacheKey}", cacheKey);
            Delete(cacheKey);
            return null;
        }
    }

    /// <summary>
    /// MP3 データとメタデータをディスクに保存する。
    /// </summary>
    public async Task SaveAsync(VoiceRequest request, byte[] mp3Data, string cacheKey, CancellationToken cancellationToken = default)
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

        await File.WriteAllBytesAsync(audioPath, mp3Data, cancellationToken);

        var metaJson = JsonSerializer.Serialize(metadata, JsonSerializerOptionsCache.Indented);
        await File.WriteAllTextAsync(metaPath, metaJson, cancellationToken);

        _logger.LogDebug("キャッシュファイルを保存しました: {CacheKey}, サイズ: {Size} bytes", cacheKey, mp3Data.Length);
    }

    /// <summary>
    /// MP3 と meta.json のペアを削除。
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
    /// キャッシュディレクトリ内の MP3 ファイル一覧を取得。
    /// </summary>
    public string[] GetAudioFiles()
    {
        try
        {
            return System.IO.Directory.GetFiles(_settings.Directory, "*.mp3");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ファイル一覧の取得に失敗: {Directory}", _settings.Directory);
            return Array.Empty<string>();
        }
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
    public static bool IsMetadataValid(CacheMetadata metadata)
    {
        return !String.IsNullOrWhiteSpace(metadata.Text)
            && metadata.SpeakerId >= 0
            && metadata.CreatedAt != default;
    }

    private (string AudioPath, string MetaPath) ResolvePaths(string cacheKey)
    {
        var audio = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
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
