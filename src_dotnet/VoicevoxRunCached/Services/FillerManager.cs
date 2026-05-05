using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class FillerManager
{
    private readonly int _defaultSpeaker;
    private readonly FillerSettings _settings;
    private readonly ILogger _logger;
    private string? _lastUsedFiller;

    public FillerManager(FillerSettings settings, int defaultSpeakerId, ILogger? logger = null)
    {
        _settings = settings;
        _defaultSpeaker = defaultSpeakerId;
        _logger = logger ?? NullLogger.Instance;
        ResolveFillerBaseDirectory();

        _logger.LogInformation("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}",
            _settings.Enabled, _settings.Directory);
    }

    public async Task InitializeFillerCacheAsync(VoiceVoxApiClient apiClient)
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

        await apiClient.InitializeSpeakerAsync(_defaultSpeaker);

        int processed = 0;
        int total = _settings.FillerTexts.Length;

        foreach (var fillerText in _settings.FillerTexts)
        {
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            _logger.LogDebug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = BuildFillerRequest(fillerText);

            var cacheKey = AudioCacheManager.ComputeCacheKey(fillerRequest);
            var fillerCacheMp3 = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
            var fillerCacheWav = Path.Combine(_settings.Directory, $"{cacheKey}.wav");

            if (File.Exists(fillerCacheMp3) || File.Exists(fillerCacheWav))
            {
                _logger.LogDebug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
                continue;
            }

            try
            {
                var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId);

                await SaveFillerAudioAsync(fillerCacheMp3, audioData);
                _logger.LogDebug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);
            }
        }

        _logger.LogInformation("フィラーキャッシュの初期化が完了しました - 項目数: {Count}", _settings.FillerTexts.Length);
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync()
    {
        if (!_settings.Enabled || _settings.FillerTexts.Length == 0)
        {
            _logger.LogDebug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        string selectedFiller = SelectFillerText();
        _lastUsedFiller = selectedFiller;
        _logger.LogDebug("ランダムフィラー音声を選択: \"{Text}\"", selectedFiller);

        var fillerRequest = BuildFillerRequest(selectedFiller);
        var cacheKey = AudioCacheManager.ComputeCacheKey(fillerRequest);
        var fillerCacheMp3 = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
        var fillerCacheWav = Path.Combine(_settings.Directory, $"{cacheKey}.wav");

        var data = await TryReadAsync(fillerCacheMp3) ?? await TryReadAsync(fillerCacheWav);
        if (data == null)
        {
            _logger.LogWarning("フィラーキャッシュファイルが見つかりませんでした - text=\"{Text}\"", selectedFiller);
        }
        return data;
    }

    private async Task<byte[]?> TryReadAsync(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(path);
            _logger.LogDebug("フィラーキャッシュファイルを読み込みました: {Path} (サイズ: {Size} bytes)",
                Path.GetFileName(path), data.Length);
            return data;
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

        var availableFillers = _settings.FillerTexts
            .Where(f => f != _lastUsedFiller)
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

    private async Task SaveFillerAudioAsync(string filePath, byte[] audioData)
    {
        var converted = await ConvertWavToMp3Async(audioData);
        bool isMp3 = converted.Length >= 2 && converted[0] == 0xFF && (converted[1] & 0xE0) == 0xE0;
        bool isWav = converted.Length >= 12 &&
                     converted[0] == 'R' && converted[1] == 'I' && converted[2] == 'F' && converted[3] == 'F' &&
                     converted[8] == 'W' && converted[9] == 'A' && converted[10] == 'V' && converted[11] == 'E';

        var targetPath = filePath;
        if (!isMp3 && isWav)
        {
            targetPath = Path.ChangeExtension(filePath, ".wav");
        }
        await File.WriteAllBytesAsync(targetPath, converted);
    }

    private static async Task<byte[]> ConvertWavToMp3Async(byte[] wavData)
    {
        try
        {
            return await Task.Run(() => AudioConversionUtility.ConvertWavToMp3(wavData));
        }
        catch
        {
            return wavData;
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

    private void ResolveFillerBaseDirectory()
    {
        try
        {
            if (_settings.UseExecutableBaseDirectory && !Path.IsPathRooted(_settings.Directory))
            {
                var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
                var combined = Path.Combine(executableDirectory, _settings.Directory);
                _settings.Directory = Path.GetFullPath(combined);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve filler base directory");
        }
    }
}
