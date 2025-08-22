using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 単一音声ファイルの再生制御を行うクラス
/// </summary>
public class AudioPlaybackController : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly AudioFormatDetector _formatDetector;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;

    public AudioPlaybackController(AudioSettings settings, AudioFormatDetector formatDetector)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
    }

    /// <summary>
    /// 音声データを再生します
    /// </summary>
    /// <param name="audioData">再生する音声データ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlaybackController));

        try
        {
            this.StopAudio();

            // フォーマット検出とWaveStream作成
            var reader = await this._formatDetector.CreateWaveStreamAsync(audioData);

            // WavePlayerの作成
            this._wavePlayer = this.CreateWavePlayer();
            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            var tcs = new TaskCompletionSource<bool>();

            // 再生完了イベントの登録
            EventHandler<StoppedEventArgs>? handler = null;
            handler = (sender, e) =>
            {
                try { reader?.Dispose(); } catch { }
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
                if (this._wavePlayer != null && handler != null)
                {
                    this._wavePlayer.PlaybackStopped -= handler;
                }
            };
            this._wavePlayer.PlaybackStopped += handler;

            // 音声の初期化
            this._wavePlayer.Init(reader);

            // 音声初期化のための最小遅延
            await Task.Delay(20, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            this._wavePlayer.Play();

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
            this.StopAudio();
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
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlaybackController));

        try
        {
            this.StopAudio();

            // フォーマット検出とWaveStream作成
            var reader = await this._formatDetector.CreateWaveStreamAsync(audioData);

            // WavePlayerの作成
            this._wavePlayer = this.CreateWavePlayer();
            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

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
            handler = (sender, e) =>
            {
                try { reader?.Dispose(); } catch { }
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
                if (this._wavePlayer != null && handler != null)
                {
                    this._wavePlayer.PlaybackStopped -= handler;
                }
            };
            this._wavePlayer.PlaybackStopped += handler;

            // 音声の初期化
            this._wavePlayer.Init(reader);

            // 音声初期化のための最小遅延
            await Task.Delay(20, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            this._wavePlayer.Play();

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
            this.StopAudio();
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

        if (this._settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = this._settings.OutputDevice;
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
            if (this._wavePlayer != null)
            {
                this._wavePlayer.Stop();
                this._wavePlayer.Dispose();
                this._wavePlayer = null;
            }
        }
        catch
        {
            // 停止時のエラーは無視
        }
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this.StopAudio();
                this._disposed = true;
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

    // ファイナライザー（安全性のため）
    ~AudioPlaybackController()
    {
        this.Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this._disposed)
        {
            if (disposing)
            {
                // マネージドリソースの破棄
                this.Dispose();
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
