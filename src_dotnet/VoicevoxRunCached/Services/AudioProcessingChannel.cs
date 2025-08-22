using System.Threading.Channels;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using Serilog;

namespace VoicevoxRunCached.Services;

public class AudioProcessingChannel : IDisposable
{
    private readonly AudioCacheManager _cacheManager;
    private readonly VoiceVoxApiClient _apiClient;
    private bool _disposed;

    public AudioProcessingChannel(AudioCacheManager cacheManager, VoiceVoxApiClient apiClient)
    {
        this._cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        this._apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

        Log.Information("AudioProcessingChannel を初期化しました - 同期処理モード");
    }

    public async Task<AudioProcessingResult> ProcessAudioAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var task = new AudioProcessingTask
        {
            Id = Guid.NewGuid(),
            Request = request,
            Timestamp = DateTime.UtcNow
        };

        Log.Debug("音声処理タスクを開始: {TaskId}", task.Id);

        // まずキャッシュをチェック
        var cachedAudio = await this._cacheManager.GetCachedAudioAsync(request);
        if (cachedAudio != null)
        {
            Log.Debug("キャッシュから音声を取得: {TaskId}", task.Id);
            return new AudioProcessingResult
            {
                TaskId = task.Id,
                AudioData = cachedAudio,
                IsFromCache = true,
                ProcessingTime = DateTime.UtcNow - task.Timestamp,
                Success = true
            };
        }

        // キャッシュにない場合はAPIから生成
        try
        {
            Log.Debug("APIから音声を生成開始: {TaskId}", task.Id);
            var audioQuery = await this._apiClient.GenerateAudioQueryAsync(request, cancellationToken);
            var audioData = await this._apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);

            // キャッシュに保存
            await this._cacheManager.SaveAudioCacheAsync(request, audioData);

            Log.Debug("APIから音声を生成完了: {TaskId}, サイズ: {Size} bytes", task.Id, audioData.Length);
            return new AudioProcessingResult
            {
                TaskId = task.Id,
                AudioData = audioData,
                IsFromCache = false,
                ProcessingTime = DateTime.UtcNow - task.Timestamp,
                Success = true
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音声処理タスクが失敗: {TaskId}", task.Id);
            return new AudioProcessingResult
            {
                TaskId = task.Id,
                AudioData = Array.Empty<byte>(),
                IsFromCache = false,
                ProcessingTime = DateTime.UtcNow - task.Timestamp,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public void Dispose()
    {
        if (this._disposed) return;

        try
        {
            // 同期処理なので特別なクリーンアップは不要
            Log.Debug("AudioProcessingChannel の破棄処理を開始");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AudioProcessingChannel の破棄中にエラーが発生しました");
        }
        finally
        {
            this._disposed = true;
        }

        Log.Debug("AudioProcessingChannel を破棄しました");
    }
}

public class AudioProcessingTask
{
    public Guid Id { get; set; }
    public VoiceRequest Request { get; set; } = null!;
    public DateTime Timestamp { get; set; }
}

public class AudioProcessingResult
{
    public Guid TaskId { get; set; }
    public byte[] AudioData { get; set; } = Array.Empty<byte>();
    public bool IsFromCache { get; set; }
    public TimeSpan ProcessingTime { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
