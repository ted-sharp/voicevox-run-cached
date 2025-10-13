using NAudio.Wave;
using Serilog;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 単一音声ファイルの再生制御を行うクラス
/// </summary>
public class AudioPlaybackController : IDisposable
{
    private readonly AudioFormatDetector _formatDetector;
    private readonly AudioSettings _settings;
    private bool _disposed;
    private IWavePlayer? _wavePlayer;

    public AudioPlaybackController(AudioSettings settings, AudioFormatDetector formatDetector)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                StopAudio();
                _disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AudioPlaybackControllerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// 音声データを再生します
    /// </summary>
    /// <param name="audioData">再生する音声データ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlaybackController));

        try
        {
            StopAudio();

            // フォーマット検出とWaveStream作成
            var reader = await _formatDetector.CreateWaveStreamAsync(audioData);

            // WavePlayerの作成
            _wavePlayer = CreateWavePlayer();
            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

            var tcs = new TaskCompletionSource<bool>();

            // 再生完了イベントの登録
            EventHandler<StoppedEventArgs>? handler = null;
            handler = (_, e) =>
            {
                try
                { reader?.Dispose(); }
                catch (Exception ex)
                { Log.Debug(ex, "Failed to dispose audio reader in PlaybackStopped handler"); }
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
                if (_wavePlayer != null && handler != null)
                {
                    _wavePlayer.PlaybackStopped -= handler;
                }
            };
            _wavePlayer.PlaybackStopped += handler;

            // 音声の初期化
            _wavePlayer.Init(reader);

            // 音声初期化のための最小遅延
            await Task.Delay(20, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _wavePlayer.Play();

            // 再生完了を待機
            await tcs.Task.ConfigureAwait(false);

            // バッファがフラッシュされるまで待機
            await Task.Delay(150, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio: {ex.Message}", ex);
        }
        finally
        {
            StopAudio();
        }
    }

    /// <summary>
    /// 音声データをストリーミング再生します
    /// </summary>
    /// <param name="audioData">再生する音声データ</param>
    /// <param name="cacheCallback">キャッシュ保存用コールバック</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlaybackController));

        try
        {
            StopAudio();

            // フォーマット検出とWaveStream作成
            var reader = await _formatDetector.CreateWaveStreamAsync(audioData);

            // WavePlayerの作成
            _wavePlayer = CreateWavePlayer();
            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

            var tcs = new TaskCompletionSource<bool>();

            // キャッシュ保存を並行で開始（コールバックが提供されている場合）
            Task? cacheTask = null;
            if (cacheCallback != null)
            {
                cacheTask = Task.Run(async () =>
                {
                    try
                    {
                        await cacheCallback(audioData);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "音声のキャッシュ保存に失敗しました");
                    }
                });
            }

            // 再生完了イベントの登録
            EventHandler<StoppedEventArgs>? handler = null;
            handler = (_, e) =>
            {
                try
                { reader?.Dispose(); }
                catch (Exception ex)
                { Log.Debug(ex, "Failed to dispose audio reader in PlaybackStopped handler"); }
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
                if (_wavePlayer != null && handler != null)
                {
                    _wavePlayer.PlaybackStopped -= handler;
                }
            };
            _wavePlayer.PlaybackStopped += handler;

            // 音声の初期化
            _wavePlayer.Init(reader);

            // 音声初期化のための最小遅延
            await Task.Delay(20, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _wavePlayer.Play();

            // 再生完了を待機
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            // バッファがフラッシュされるまで待機
            await Task.Delay(150, cancellationToken);

            // キャッシュタスクの完了を待機
            if (cacheTask != null)
            {
                await cacheTask;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio: {ex.Message}", ex);
        }
        finally
        {
            StopAudio();
        }
    }

    /// <summary>
    /// 基本的なWavePlayerを作成します
    /// </summary>
    /// <returns>作成されたWavePlayer</returns>
    private IWavePlayer CreateWavePlayer()
    {
        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };

        if (_settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = _settings.OutputDevice;
        }

        return waveOut;
    }

    /// <summary>
    /// 音声再生を停止します
    /// </summary>
    public void StopAudio()
    {
        try
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.Stop();
                _wavePlayer.Dispose();
                _wavePlayer = null;
            }
        }
        catch
        {
            // 停止時のエラーは無視
        }
    }

    // ファイナライザー（安全性のため）
    ~AudioPlaybackController()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // マネージドリソースの破棄
                Dispose();
            }
            else
            {
                // ファイナライザーが呼ばれた場合 - アンマネージドリソースのみ破棄
                try
                {
                    // アンマネージドリソースの破棄
                }
                catch
                {
                    // ファイナライザーでのエラーは無視
                }
            }
        }
    }
}
