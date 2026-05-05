using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services.Synthesis;

public class FillerManager
{
    private readonly int _defaultSpeaker;
    private readonly FillerSettings _settings;
    private readonly ILogger _logger;
    // 直近フィラー記録は singleton 状態。consumer は基本 1 スレッドだが、将来並列化が
    // 入っても tearing しないよう Volatile.Read/Write で guard する。
    private string? _lastUsedFiller;

    public FillerManager(FillerSettings settings, int defaultSpeakerId, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _defaultSpeaker = defaultSpeakerId;
        _logger = logger ?? NullLogger.Instance;

        _logger.LogInformation("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}",
            _settings.Enabled, _settings.Directory);
    }

    public async Task InitializeFillerCacheAsync(VoiceVoxApiClient apiClient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiClient);

        if (!_settings.Enabled)
        {
            _logger.LogInformation("フィラー機能が無効化されているため、初期化をスキップします");
            return;
        }

        Directory.CreateDirectory(_settings.Directory);
        _logger.LogInformation("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", _settings.FillerTexts.Length);

        using var spinner = new ProgressSpinner("Initializing filler cache...");

        await apiClient.InitializeSpeakerAsync(_defaultSpeaker, cancellationToken);

        int processed = 0;
        int total = _settings.FillerTexts.Length;

        foreach (var fillerText in _settings.FillerTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            _logger.LogDebug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = BuildFillerRequest(fillerText);
            var (fillerCacheMp3, fillerCacheWav) = ResolveFillerCachePaths(fillerRequest);

            if (File.Exists(fillerCacheMp3) || File.Exists(fillerCacheWav))
            {
                _logger.LogDebug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
                continue;
            }

            try
            {
                var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest, cancellationToken);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId, cancellationToken);

                await SaveFillerAudioAsync(fillerCacheMp3, audioData, cancellationToken);
                _logger.LogDebug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);
            }
        }

        _logger.LogInformation("フィラーキャッシュの初期化が完了しました - 項目数: {Count}", _settings.FillerTexts.Length);
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _settings.FillerTexts.Length == 0)
        {
            _logger.LogDebug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        string selectedFiller = SelectFillerText();
        System.Threading.Volatile.Write(ref _lastUsedFiller, selectedFiller);
        _logger.LogDebug("ランダムフィラー音声を選択: \"{Text}\"", selectedFiller);

        var fillerRequest = BuildFillerRequest(selectedFiller);
        var (fillerCacheMp3, fillerCacheWav) = ResolveFillerCachePaths(fillerRequest);

        var data = await TryReadAsync(fillerCacheMp3, cancellationToken)
            ?? await TryReadAsync(fillerCacheWav, cancellationToken);
        if (data == null)
        {
            _logger.LogWarning("フィラーキャッシュファイルが見つかりませんでした - text=\"{Text}\"", selectedFiller);
        }
        return data;
    }

    private async Task<byte[]?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(path, cancellationToken);
            _logger.LogDebug("フィラーキャッシュファイルを読み込みました: {Path} (サイズ: {Size} bytes)",
                Path.GetFileName(path), data.Length);
            return data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "フィラー音声の読み込みに失敗しました: {Path}", path);
            return null;
        }
    }

    private string SelectFillerText()
    {
        if (_settings.FillerTexts.Length == 1)
        {
            return _settings.FillerTexts[0];
        }

        var lastUsed = System.Threading.Volatile.Read(ref _lastUsedFiller);
        var availableFillers = _settings.FillerTexts
            .Where(f => f != lastUsed)
            .ToArray();

        if (availableFillers.Length == 0)
        {
            availableFillers = _settings.FillerTexts;
        }

        return availableFillers[Random.Shared.Next(availableFillers.Length)];
    }

    private VoiceRequest BuildFillerRequest(string text) => new()
    {
        Text = text,
        SpeakerId = _defaultSpeaker,
        Speed = 1.0,
        Pitch = 0.0,
        Volume = 1.0
    };

    /// <summary>
    /// フィラーキャッシュ用の MP3/WAV パスを返す。MP3 が標準で、変換失敗時のみ WAV が存在する。
    /// </summary>
    private (string Mp3Path, string WavPath) ResolveFillerCachePaths(VoiceRequest fillerRequest)
    {
        var cacheKey = CacheKeyGenerator.Compute(fillerRequest);
        var mp3 = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
        var wav = Path.Combine(_settings.Directory, $"{cacheKey}.wav");
        return (mp3, wav);
    }

    private async Task SaveFillerAudioAsync(string filePath, byte[] audioData, CancellationToken cancellationToken)
    {
        try
        {
            var mp3 = await Task.Run(() => AudioConversionUtility.ConvertWavToMp3(audioData), cancellationToken);
            await File.WriteAllBytesAsync(filePath, mp3, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 変換失敗時は WAV のまま .wav 拡張子で保存する。
            // 旧実装は silent fallback だったが観測性のため Warning で必ず残す。
            _logger.LogWarning(ex, "フィラー音声の MP3 変換に失敗。WAV 形式で保存します");
            var wavPath = Path.ChangeExtension(filePath, ".wav");
            await File.WriteAllBytesAsync(wavPath, audioData, cancellationToken);
        }
    }

    public Task ClearFillerCacheAsync()
    {
        try
        {
            if (!Directory.Exists(_settings.Directory))
                return Task.CompletedTask;

            foreach (var pattern in new[] { "*.mp3", "*.wav" })
            {
                var files = Directory.GetFiles(_settings.Directory, pattern);
                foreach (var file in files)
                {
                    try
                    { File.Delete(file); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete file: {File}", file); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear filler cache directory");
        }
        return Task.CompletedTask;
    }
}
