using NAudio.Wave;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using Serilog;

namespace VoicevoxRunCached.Services;

public class FillerManager
{
    private readonly FillerSettings _settings;
    private readonly AudioCacheManager _cacheManager;
    private readonly int _defaultSpeaker;

    public FillerManager(FillerSettings settings, AudioCacheManager cacheManager, int defaultSpeakerId)
    {
        this._settings = settings;
        this._cacheManager = cacheManager;
        this._defaultSpeaker = defaultSpeakerId;
        this.ResolveFillerBaseDirectory();

        Log.Information("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}", this._settings.Enabled, this._settings.Directory);
    }

    public async Task InitializeFillerCacheAsync(AppSettings appSettings)
    {
        if (!this._settings.Enabled)
        {
            Log.Information("フィラー機能が無効化されているため、初期化をスキップします");
            return;
        }

        Directory.CreateDirectory(this._settings.Directory);
        Log.Information("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", this._settings.FillerTexts.Length);

        using var spinner = new ProgressSpinner("Initializing filler cache...");
        using var apiClient = new VoiceVoxApiClient(appSettings.VoiceVox);

        await apiClient.InitializeSpeakerAsync(this._defaultSpeaker);

        int processed = 0;
        int total = this._settings.FillerTexts.Length;

        foreach (var fillerText in this._settings.FillerTexts)
        {
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            Log.Debug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = new VoiceRequest
            {
                Text = fillerText,
                SpeakerId = this._defaultSpeaker,
                Speed = 1.0,
                Pitch = 0.0,
                Volume = 1.0
            };

            // Check if already cached (mp3 or wav)
            var cacheKey = this._cacheManager.ComputeCacheKey(fillerRequest);
            var fillerCacheMp3 = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
            var fillerCacheWav = Path.Combine(this._settings.Directory, $"{cacheKey}.wav");

            if (!File.Exists(fillerCacheMp3) && !File.Exists(fillerCacheWav))
            {
                try
                {
                    var audioQuery = await apiClient.GenerateAudioQueryAsync(fillerRequest);
                    var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, fillerRequest.SpeakerId);

                    // Save directly to filler directory
                    await this.SaveFillerAudioAsync(fillerCacheMp3, audioData);
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
        Log.Information("フィラーキャッシュの初期化が完了しました - 項目数: {Count}", this._settings.FillerTexts.Length);
    }

    public async Task<byte[]?> GetRandomFillerAudioAsync()
    {
        if (!this._settings.Enabled || this._settings.FillerTexts.Length == 0)
        {
            Log.Debug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        var random = new Random();
        var randomFiller = this._settings.FillerTexts[random.Next(this._settings.FillerTexts.Length)];
        Log.Debug("ランダムフィラー音声を選択: \"{Text}\"", randomFiller);

        var fillerRequest = new VoiceRequest
        {
            Text = randomFiller,
            SpeakerId = this._defaultSpeaker,
            Speed = 1.0,
            Pitch = 0.0,
            Volume = 1.0
        };

        var cacheKey = this._cacheManager.ComputeCacheKey(fillerRequest);
        var fillerCacheMp3 = Path.Combine(this._settings.Directory, $"{cacheKey}.mp3");
        var fillerCacheWav = Path.Combine(this._settings.Directory, $"{cacheKey}.wav");

        if (File.Exists(fillerCacheMp3))
        {
            try
            {
                return await File.ReadAllBytesAsync(fillerCacheMp3);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "フィラー音声の読み込みに失敗しました");
            }
        }

        if (File.Exists(fillerCacheWav))
        {
            try
            {
                return await File.ReadAllBytesAsync(fillerCacheWav);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "フィラー音声の読み込みに失敗しました");
            }
        }

        return null;
    }

    private async Task SaveFillerAudioAsync(string filePath, byte[] audioData)
    {
        var converted = await this.ConvertWavToMp3Async(audioData);
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
            using var wavStream = new MemoryStream(wavData);
            using var waveReader = new WaveFileReader(wavStream);
            using var outputStream = new MemoryStream();
            MediaFoundationManager.EnsureInitialized();
            MediaFoundationEncoder.EncodeToMp3(waveReader, outputStream, 128000);
            return await Task.FromResult(outputStream.ToArray());
        }
        catch
        {
            // フォーマット判定に失敗した場合はWAVを返す（呼び出し側で適切な拡張子に保存）
            return await Task.FromResult(wavData);
        }
    }

    public Task ClearFillerCacheAsync()
    {
        try
        {
            if (!Directory.Exists(this._settings.Directory))
                return Task.CompletedTask;

            foreach (var pattern in new[] { "*.mp3", "*.wav" })
            {
                var files = Directory.GetFiles(this._settings.Directory, pattern);
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
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
            if (this._settings.UseExecutableBaseDirectory && !Path.IsPathRooted(this._settings.Directory))
            {
                var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
                var combined = Path.Combine(executableDirectory, this._settings.Directory);
                this._settings.Directory = Path.GetFullPath(combined);
            }
        }
        catch
        {
        }
    }
}
