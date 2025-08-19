using System.Threading.Channels;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using Serilog;

namespace VoicevoxRunCached.Services;

public class AudioProcessingChannel : IDisposable
{
    private readonly Channel<AudioProcessingTask> _processingChannel;
    private readonly Channel<AudioProcessingResult> _resultChannel;
    private readonly AudioCacheManager _cacheManager;
    private readonly VoiceVoxApiClient _apiClient;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    private readonly Task _resultTask;
    private bool _disposed;

    public AudioProcessingChannel(AudioCacheManager cacheManager, VoiceVoxApiClient apiClient)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));

        // バウンドチャンネルを作成（メモリ使用量を制限）
        var channelOptions = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _processingChannel = Channel.CreateBounded<AudioProcessingTask>(channelOptions);
        _resultChannel = Channel.CreateBounded<AudioProcessingResult>(channelOptions);
        _cancellationTokenSource = new CancellationTokenSource();

        // バックグラウンド処理タスクを開始
        _processingTask = Task.Run(ProcessAudioTasksAsync);
        _resultTask = Task.Run(ProcessResultsAsync);

        Log.Information("AudioProcessingChannel を初期化しました - 最大キューサイズ: 100");
    }

    public async Task<AudioProcessingResult> ProcessAudioAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var task = new AudioProcessingTask
        {
            Id = Guid.NewGuid(),
            Request = request,
            Timestamp = DateTime.UtcNow
        };

        // 処理キューに追加
        await _processingChannel.Writer.WriteAsync(task, cancellationToken);

        // 結果を待機
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);

        while (await _resultChannel.Reader.WaitToReadAsync(linkedCts.Token))
        {
            if (_resultChannel.Reader.TryRead(out var result) && result.TaskId == task.Id)
            {
                return result;
            }
        }

        throw new OperationCanceledException("音声処理がキャンセルされました");
    }

    private async Task ProcessAudioTasksAsync()
    {
        try
        {
            await foreach (var task in _processingChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                try
                {
                    Log.Debug("音声処理タスクを開始: {TaskId}", task.Id);

                    // まずキャッシュをチェック
                    var cachedAudio = await _cacheManager.GetCachedAudioAsync(task.Request);
                    if (cachedAudio != null)
                    {
                        var result = new AudioProcessingResult
                        {
                            TaskId = task.Id,
                            AudioData = cachedAudio,
                            IsFromCache = true,
                            ProcessingTime = DateTime.UtcNow - task.Timestamp,
                            Success = true
                        };

                        await _resultChannel.Writer.WriteAsync(result, _cancellationTokenSource.Token);
                        Log.Debug("キャッシュから音声を取得: {TaskId}", task.Id);
                        continue;
                    }

                    // キャッシュにない場合はAPIから生成
                    var audioQuery = await _apiClient.GenerateAudioQueryAsync(task.Request, _cancellationTokenSource.Token);
                    var audioData = await _apiClient.SynthesizeAudioAsync(audioQuery, task.Request.SpeakerId, _cancellationTokenSource.Token);

                    // キャッシュに保存
                    await _cacheManager.SaveAudioCacheAsync(task.Request, audioData);

                    var apiResult = new AudioProcessingResult
                    {
                        TaskId = task.Id,
                        AudioData = audioData,
                        IsFromCache = false,
                        ProcessingTime = DateTime.UtcNow - task.Timestamp,
                        Success = true
                    };

                    await _resultChannel.Writer.WriteAsync(apiResult, _cancellationTokenSource.Token);
                    Log.Debug("APIから音声を生成: {TaskId}", task.Id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "音声処理タスクが失敗: {TaskId}", task.Id);

                    var errorResult = new AudioProcessingResult
                    {
                        TaskId = task.Id,
                        AudioData = Array.Empty<byte>(),
                        IsFromCache = false,
                        ProcessingTime = DateTime.UtcNow - task.Timestamp,
                        Success = false,
                        ErrorMessage = ex.Message
                    };

                    await _resultChannel.Writer.WriteAsync(errorResult, _cancellationTokenSource.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("音声処理タスクがキャンセルされました");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音声処理タスクで予期しないエラーが発生しました");
        }
    }

    private async Task ProcessResultsAsync()
    {
        try
        {
            await foreach (var result in _resultChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                // 結果の後処理（必要に応じて）
                if (result.Success)
                {
                    Log.Debug("音声処理結果を処理: {TaskId}, 処理時間: {ProcessingTime}ms",
                        result.TaskId, result.ProcessingTime.TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("結果処理タスクがキャンセルされました");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "結果処理タスクで予期しないエラーが発生しました");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _cancellationTokenSource.Cancel();
            _processingChannel.Writer.Complete();
            _resultChannel.Writer.Complete();

            // タスクの完了を待機（タイムアウト付き）
            var timeout = TimeSpan.FromSeconds(5);
            Task.WaitAll(new[] { _processingTask, _resultTask }, timeout);

            _cancellationTokenSource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AudioProcessingChannel の破棄中にエラーが発生しました");
        }
        finally
        {
            _disposed = true;
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
