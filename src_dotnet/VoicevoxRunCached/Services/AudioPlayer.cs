using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声再生の統合制御を行うクラス
/// 具体的な実装は各専門クラスに委譲します
/// </summary>
public class AudioPlayer : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly AudioPlaybackController _playbackController;
    private readonly AudioSegmentPlayer _segmentPlayer;
    private bool _disposed;

    public AudioPlayer(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 各専門クラスのインスタンスを作成
        var formatDetector = new AudioFormatDetector();
        _deviceManager = new AudioDeviceManager(settings);
        _playbackController = new AudioPlaybackController(settings, formatDetector);
        _segmentPlayer = new AudioSegmentPlayer(settings, formatDetector);

        Log.Information("AudioPlayer を初期化しました - 音量: {Volume}, デバイス: {Device}",
            settings.Volume, settings.OutputDevice);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await _deviceManager.EnsureDeviceReadyAsync();

        // 再生制御クラスに委譲
        await _playbackController.PlayAudioAsync(audioData, cacheCallback, cancellationToken);
    }

    /// <summary>
    /// 音声セグメントのリストを順次再生します
    /// </summary>
    /// <param name="audioSegments">再生する音声セグメントのリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>再生完了を表すTask</returns>
    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await _deviceManager.EnsureDeviceReadyAsync();

        // セグメント再生クラスに委譲
        await _segmentPlayer.PlayAudioSequentiallyAsync(audioSegments, cancellationToken);
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
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await _deviceManager.EnsureDeviceReadyAsync();

        // セグメント再生クラスに委譲
        await _segmentPlayer.PlayAudioSequentiallyWithGenerationAsync(segments, processingChannel, fillerManager, cancellationToken);
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
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // デバイスの準備完了を確認
        await _deviceManager.EnsureDeviceReadyAsync();

        // 再生制御クラスに委譲
        await _playbackController.PlayAudioAsync(audioData, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 音声再生を停止します
    /// </summary>
    public void StopAudio()
    {
        try
        {
            _playbackController.StopAudio();
            _segmentPlayer.StopAudio();
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

    // ファイナライザー（安全性のため）
    ~AudioPlayer()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    // 各専門クラスの破棄
                    _deviceManager.Dispose();
                    _playbackController.Dispose();
                    _segmentPlayer.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "AudioPlayerの破棄中にエラーが発生しました");
                }
            }

            _disposed = true;
        }
    }
}
