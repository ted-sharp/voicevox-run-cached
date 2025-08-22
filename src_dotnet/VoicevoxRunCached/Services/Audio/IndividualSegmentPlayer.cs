using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;
using Serilog;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 個別の音声セグメントを再生するクラス
/// </summary>
public class IndividualSegmentPlayer
{
    private readonly AudioSettings _settings;
    private readonly AudioFormatDetector _formatDetector;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;

    public IndividualSegmentPlayer(AudioSettings settings, AudioFormatDetector formatDetector)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
    }

    /// <summary>
    /// 個別のセグメントを再生します
    /// </summary>
    /// <param name="audioData">再生する音声データ</param>
    /// <param name="isFirstSegment">最初のセグメントかどうか</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment = false, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(IndividualSegmentPlayer));

        WaveStream? reader = null;
        try
        {
            Log.Debug("PlaySegmentAsync 開始 - サイズ: {Size} bytes, 最初のセグメント: {IsFirst}",
                audioData.Length, isFirstSegment);

            // フォーマット検出とWaveStream作成
            reader = await this._formatDetector.CreateWaveStreamAsync(audioData);

            var tcs = new TaskCompletionSource<bool>();

            if (this._wavePlayer != null)
            {
                EventHandler<StoppedEventArgs>? handler = null;
                handler = (sender, e) =>
                {
                    Log.Debug("PlaybackStopped イベントが発生しました - 例外: {Exception}",
                        e.Exception?.Message ?? "なし");
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
                Log.Debug("PlaybackStopped イベントハンドラーを登録しました");
            }

            Log.Debug("WavePlayer に音声リーダーを初期化中...");
            this._wavePlayer?.Init(reader);

            // セグメントタイプに応じた遅延を実行
            await this.ExecuteSegmentDelayAsync(isFirstSegment, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Log.Debug("音声再生を開始します");
            this._wavePlayer?.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                // タイムアウトを設定して無限待機を防ぐ
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                Log.Debug("音声再生完了を待機中 (タイムアウト: 30秒)...");
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Warning("セグメント再生がタイムアウトしました。強制停止します。");
                    this._wavePlayer?.Stop();
                    throw new TimeoutException("セグメント再生がタイムアウトしました");
                }

                Log.Debug("音声再生が完了しました。実際の完了を待機中...");
                await tcs.Task; // 実際の完了を待機
            }

            // 完全な音声再生を確保 - 適切な遅延を設定
            var playbackDelay = this.CalculatePlaybackDelay(isFirstSegment);
            Log.Debug("音声再生完了後の遅延を実行中: {Delay}ms", playbackDelay);
            await Task.Delay(playbackDelay, cancellationToken);

            // 停止するがWavePlayerは破棄しない - 次のセグメントで再利用
            Log.Debug("WavePlayer を停止中...");
            this._wavePlayer?.Stop();

            Log.Information("セグメント再生が完了しました (遅延: {Delay}ms)", playbackDelay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント再生中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
        finally
        {
            // 再生が完了してからリーダーを破棄
            try { reader?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// セグメントタイプに応じた遅延を実行します
    /// </summary>
    /// <param name="isFirstSegment">最初のセグメントかどうか</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>遅延完了を表すTask</returns>
    private async Task ExecuteSegmentDelayAsync(bool isFirstSegment, CancellationToken cancellationToken)
    {
        if (isFirstSegment)
        {
            // 最初のセグメントのため、音声デバイス初期化と安定性のための延長遅延
            Log.Debug("最初のセグメントのため、200ms の遅延を実行中...");
            await Task.Delay(200, cancellationToken); // 200ms for device initialization and stability
        }
        else
        {
            // 後続セグメントのための最小遅延
            Log.Debug("後続セグメントのため、20ms の遅延を実行中...");
            await Task.Delay(20, cancellationToken); // 20msに増加して安定性を向上
        }
    }

    /// <summary>
    /// 再生完了後の遅延時間を計算します
    /// </summary>
    /// <param name="isFirstSegment">最初のセグメントかどうか</param>
    /// <returns>遅延時間（ミリ秒）</returns>
    private int CalculatePlaybackDelay(bool isFirstSegment)
    {
        return isFirstSegment ? 150 : 100; // 最初のセグメントは長めの遅延
    }

    /// <summary>
    /// WavePlayerを設定します
    /// </summary>
    /// <param name="wavePlayer">設定するWavePlayer</param>
    public void SetWavePlayer(IWavePlayer wavePlayer)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(IndividualSegmentPlayer));

        this._wavePlayer = wavePlayer ?? throw new ArgumentNullException(nameof(wavePlayer));
        Log.Debug("WavePlayer が設定されました");
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
                Log.Debug("音声再生を停止しました");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音声停止中にエラーが発生しました");
        }
    }

    /// <summary>
    /// 現在のWavePlayerを取得します
    /// </summary>
    /// <returns>現在のWavePlayer（設定されていない場合はnull）</returns>
    public IWavePlayer? GetCurrentWavePlayer() => this._wavePlayer;

    /// <summary>
    /// 再生状態を確認します
    /// </summary>
    /// <returns>再生中の場合はtrue</returns>
    public bool IsPlaying()
    {
        return this._wavePlayer?.PlaybackState == PlaybackState.Playing;
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
                Log.Error(ex, "IndividualSegmentPlayerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
