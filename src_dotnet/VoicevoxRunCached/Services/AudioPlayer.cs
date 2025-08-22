using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声再生の統合制御を行うクラス
/// 具体的な実装は各専門クラスに委譲します
/// </summary>
public class AudioPlayer : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly AudioDeviceManager _deviceManager;
    private readonly AudioFormatDetector _formatDetector;
    private readonly AudioPlaybackController _playbackController;
    private readonly AudioSegmentPlayer _segmentPlayer;
    private bool _disposed;

    public AudioPlayer(AudioSettings settings)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // 各専門クラスのインスタンスを作成
        this._deviceManager = new AudioDeviceManager(settings);
        this._formatDetector = new AudioFormatDetector();
        this._playbackController = new AudioPlaybackController(settings, this._formatDetector);
        this._segmentPlayer = new AudioSegmentPlayer(settings, this._formatDetector);

        Log.Information("AudioPlayer を初期化しました - 音量: {Volume}, デバイス: {Device}",
            this._settings.Volume, this._settings.OutputDevice);
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
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await this._deviceManager.EnsureDeviceReadyAsync();

        // 再生制御クラスに委譲
        await this._playbackController.PlayAudioStreamingAsync(audioData, cacheCallback, cancellationToken);
    }

    /// <summary>
    /// 音声セグメントのリストを順次再生します
    /// </summary>
    /// <param name="audioSegments">再生する音声セグメントのリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await this._deviceManager.EnsureDeviceReadyAsync();

        // セグメント再生クラスに委譲
        await this._segmentPlayer.PlayAudioSequentiallyAsync(audioSegments, cancellationToken);
    }

    /// <summary>
    /// 音声セグメントのリストを順次再生し、必要に応じて音声生成を行います
    /// </summary>
    /// <param name="segments">再生するテキストセグメントのリスト</param>
    /// <param name="processingChannel">音声処理チャンネル</param>
    /// <param name="fillerManager">フィラーマネージャー</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioSequentiallyWithGenerationAsync(List<TextSegment> segments, AudioProcessingChannel? processingChannel, FillerManager? fillerManager = null, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await this._deviceManager.EnsureDeviceReadyAsync();

        // セグメント再生クラスに委譲
        await this._segmentPlayer.PlayAudioSequentiallyWithGenerationAsync(segments, processingChannel, fillerManager, cancellationToken);
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
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await this._deviceManager.EnsureDeviceReadyAsync();

        // 再生制御クラスに委譲
        await this._playbackController.PlayAudioAsync(audioData, cancellationToken);
    }

    /// <summary>
    /// 音声再生を停止します
    /// </summary>
    public void StopAudio()
    {
        try
        {
            this._playbackController.StopAudio();
            this._segmentPlayer.StopAudio();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音声停止中にエラーが発生しました");
        }
    }

    /// <summary>
    /// 利用可能なデバイスの一覧を取得します
    /// </summary>
    /// <returns>デバイス一覧</returns>
    public static List<string> GetAvailableDevices()
    {
        return AudioDeviceManager.GetAvailableDevices();
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                // 各専門クラスの破棄
                this._deviceManager?.Dispose();
                this._playbackController?.Dispose();
                this._segmentPlayer?.Dispose();
                this._disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AudioPlayerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }

    // ファイナライザー（安全性のため）
    ~AudioPlayer()
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
