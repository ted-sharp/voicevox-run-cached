using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class FillerManager
{
    private readonly AudioCacheManager _cacheManager;
    private readonly int _defaultSpeaker;
    private readonly Random _random = new Random(); // スレッドセーフなRandomインスタンス
    private readonly FillerSettings _settings;
    private string? _lastUsedFiller; // 最後に使用したフィラーを記録

    public FillerManager(FillerSettings settings, AudioCacheManager cacheManager, int defaultSpeakerId)
    {
        _settings = settings;
        _cacheManager = cacheManager;
        _defaultSpeaker = defaultSpeakerId;
        ResolveFillerBaseDirectory();

        Log.Information("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}", _settings.Enabled, _settings.Directory);
    }

    public async Task InitializeFillerCacheAsync(AppSettings appSettings)
    {
        Log.Information("InitializeFillerCacheAsync メソッドが開始されました");

        if (!_settings.Enabled)
        {
            Log.Information("フィラー機能が無効化されているため、初期化をスキップします");
            return;
        }

        Log.Information("フィラーディレクトリを作成中: {Directory}", _settings.Directory);
        Directory.CreateDirectory(_settings.Directory);
        Log.Information("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", _settings.FillerTexts.Length);

        Log.Information("ProgressSpinnerを作成中...");
        using var spinner = new ProgressSpinner("Initializing filler cache...");
        Log.Information("VoiceVoxApiClientを作成中...");
        using var apiClient = new VoiceVoxApiClient(appSettings.VoiceVox);

        Log.Information("スピーカー {SpeakerId} を初期化中...", _defaultSpeaker);
        await apiClient.InitializeSpeakerAsync(_defaultSpeaker);
        Log.Information("スピーカー初期化が完了しました");

        int processed = 0;
        int total = _settings.FillerTexts.Length;

        foreach (var fillerText in _settings.FillerTexts)
        {
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            Log.Debug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = new VoiceRequest
            {
                Text = fillerText,
                SpeakerId = _defaultSpeaker,
                Speed = 1.0,
                Pitch = 0.0,
                Volume = 1.0
            };

            // Check if already cached (mp3 or wav)
            var cacheKey = _cacheManager.ComputeCacheKey(fillerRequest);
            var fillerCacheMp3 = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
            var fillerCacheWav = Path.Combine(_settings.Directory, $"{cacheKey}.wav");

            if (!File.Exists(fillerCacheMp3) && !File.Exists(fillerCacheWav))
            {
                try
                {
                    var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest);
                    var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId);

                    // Save directly to filler directory
                    await SaveFillerAudioAsync(fillerCacheMp3, audioData);
                    Log.Debug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);
                }
            }
            else
            {
                Log.Debug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
            }
        }

        spinner.Dispose();
        Log.Information("フィラーキャッシュの初期化が完了しました - 項目数: {Count}", _settings.FillerTexts.Length);
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync()
    {
        if (!_settings.Enabled || _settings.FillerTexts.Length == 0)
        {
            Log.Debug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        // 連続で同じフィラーが選択されないようにする
        string selectedFiller;
        if (_settings.FillerTexts.Length == 1)
        {
            selectedFiller = _settings.FillerTexts[0];
            Log.Debug("フィラーが1つしかないため、同じものを選択: \"{Text}\"", selectedFiller);
        }
        else
        {
            // 最後に使用したフィラー以外から選択
            var availableFillers = _settings.FillerTexts
                .Where(f => f != _lastUsedFiller)
                .ToArray();

            if (availableFillers.Length == 0)
            {
                // 全て使用済みの場合は全フィラーから選択
                availableFillers = _settings.FillerTexts;
                Log.Debug("全てのフィラーが使用済みのため、全フィラーから選択");
            }

            selectedFiller = availableFillers[_random.Next(availableFillers.Length)];
            Log.Debug("利用可能なフィラー数: {AvailableCount}, 選択されたフィラー: \"{Text}\"", availableFillers.Length, selectedFiller);
        }

        _lastUsedFiller = selectedFiller;
        Log.Information("ランダムフィラー音声を選択: \"{Text}\" (前回: \"{LastUsed}\")", selectedFiller, _lastUsedFiller);

        var fillerRequest = new VoiceRequest
        {
            Text = selectedFiller,
            SpeakerId = _defaultSpeaker,
            Speed = 1.0,
            Pitch = 0.0,
            Volume = 1.0
        };

        var cacheKey = _cacheManager.ComputeCacheKey(fillerRequest);
        var fillerCacheMp3 = Path.Combine(_settings.Directory, $"{cacheKey}.mp3");
        var fillerCacheWav = Path.Combine(_settings.Directory, $"{cacheKey}.wav");

        Log.Debug("フィラーキャッシュファイルを検索中 - MP3: {Mp3Path}, WAV: {WavPath}",
            Path.GetFileName(fillerCacheMp3), Path.GetFileName(fillerCacheWav));

        if (File.Exists(fillerCacheMp3))
        {
            try
            {
                Log.Debug("MP3 フィラーキャッシュファイルを読み込み中: {Path}", fillerCacheMp3);
                var audioData = await File.ReadAllBytesAsync(fillerCacheMp3);
                Log.Information("MP3 フィラーキャッシュファイルの読み込みが完了しました: {Path} (サイズ: {Size} bytes)",
                    Path.GetFileName(fillerCacheMp3), audioData.Length);
                return audioData;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MP3 フィラー音声の読み込みに失敗しました: {Path}", fillerCacheMp3);
            }
        }

        if (File.Exists(fillerCacheWav))
        {
            try
            {
                Log.Debug("WAV フィラーキャッシュファイルを読み込み中: {Path}", fillerCacheWav);
                var audioData = await File.ReadAllBytesAsync(fillerCacheWav);
                Log.Information("WAV フィラーキャッシュファイルの読み込みが完了しました: {Path} (サイズ: {Size} bytes)",
                    Path.GetFileName(fillerCacheWav), audioData.Length);
                return audioData;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WAV フィラー音声の読み込みに失敗しました: {Path}", fillerCacheWav);
            }
        }

        Log.Warning("フィラーキャッシュファイルが見つかりませんでした - MP3: {Mp3Exists}, WAV: {WavExists}",
            File.Exists(fillerCacheMp3), File.Exists(fillerCacheWav));
        return null;
    }

    private async Task SaveFillerAudioAsync(string filePath, byte[] audioData)
    {
        var converted = await ConvertWavToMp3Async(audioData);
        // Detect whether converted data is actually MP3 or WAV, then choose extension accordingly
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

    private async Task<byte[]> ConvertWavToMp3Async(byte[] wavData)
    {
        try
        {
            // Run CPU-intensive conversion on background thread
            return await Task.Run(() => AudioConversionUtility.ConvertWavToMp3(wavData));
        }
        catch
        {
            // フォーマット判定に失敗した場合はWAVを返す（呼び出し側で適切な拡張子に保存）
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
                    catch { }
                }
            }
        }
        catch
        {
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
        catch
        {
        }
    }
}
