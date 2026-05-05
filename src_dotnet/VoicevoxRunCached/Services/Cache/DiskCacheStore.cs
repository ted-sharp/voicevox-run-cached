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

    public DiskCacheStore(CacheSettings settings, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _expiration = TimeSpan.FromDays(settings.ExpirationDays);
        _logger = logger ?? NullLogger.Instance;
        ResolveBaseDirectory();
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

            if (DateTime.UtcNow - metadata.CreatedAt > _expiration)
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
    public async Task SaveAsync(VoiceRequest request, byte[] mp3Data, string cacheKey)
    {
        var (audioPath, metaPath) = ResolvePaths(cacheKey);

        var metadata = new CacheMetadata
        {
            CreatedAt = DateTime.UtcNow,
            Text = request.Text,
            SpeakerId = request.SpeakerId,
            Speed = request.Speed,
            Pitch = request.Pitch,
            Volume = request.Volume
        };

        await File.WriteAllBytesAsync(audioPath, mp3Data);

        var metaJson = JsonSerializer.Serialize(metadata, JsonSerializerOptionsCache.Indented);
        await File.WriteAllTextAsync(metaPath, metaJson);

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
    /// メタデータファイル一覧を取得。
    /// </summary>
    public string[] GetMetadataFiles()
    {
        try
        {
            return System.IO.Directory.GetFiles(_settings.Directory, "*.meta.json");
        }
        catch
        {
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
    /// メタデータを読む。失敗時 null。
    /// </summary>
    public async Task<CacheMetadata?> ReadMetadataAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var (_, metaPath) = ResolvePaths(cacheKey);
            if (!File.Exists(metaPath))
                return null;

            var json = await File.ReadAllTextAsync(metaPath, cancellationToken);
            return JsonSerializer.Deserialize<CacheMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同期版のメタデータ読み込み (eviction 内のループから利用)。
    /// </summary>
    public CacheMetadata? ReadMetadata(string metaFilePath)
    {
        try
        {
            if (!File.Exists(metaFilePath))
                return null;
            var json = File.ReadAllText(metaFilePath);
            return JsonSerializer.Deserialize<CacheMetadata>(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read metadata file: {Path}", metaFilePath);
            return null;
        }
    }

    /// <summary>
    /// ディスクキャッシュ全体の集計 (ファイル数・合計サイズ)。
    /// </summary>
    public (int TotalFiles, long TotalSize) GetStatistics()
    {
        try
        {
            var files = GetAudioFiles();
            long totalSize = 0;
            int validFiles = 0;

            foreach (var path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    totalSize += info.Length;
                    validFiles++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "キャッシュファイル情報の取得に失敗 (統計集計時): {FilePath}", path);
                }
            }

            return (validFiles, totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュ統計情報の取得に失敗しました");
            return (0, 0);
        }
    }

    /// <summary>
    /// メタデータの整合性検証 (Text が非空、SpeakerId > 0、CreatedAt が default でない)。
    /// </summary>
    public static bool IsMetadataValid(CacheMetadata metadata)
    {
        return !String.IsNullOrWhiteSpace(metadata.Text)
            && metadata.SpeakerId > 0
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

    private void ResolveBaseDirectory()
    {
        try
        {
            if (_settings.UseExecutableBaseDirectory && !Path.IsPathRooted(_settings.Directory))
            {
                var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var executableDirectory = Path.GetDirectoryName(executablePath) ?? System.IO.Directory.GetCurrentDirectory();
                var combined = Path.Combine(executableDirectory, _settings.Directory);
                _settings.Directory = Path.GetFullPath(combined);
            }
        }
        catch
        {
            // 失敗時は元の設定値を保持
        }
    }
}
