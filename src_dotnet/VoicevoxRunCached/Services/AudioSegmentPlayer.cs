using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声セグメントの順次再生を統合制御するクラス
/// 各専門クラスを統合し、セグメント再生の高レベルAPIを提供します
/// </summary>
public class AudioSegmentPlayer : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly AudioFormatDetector _formatDetector;
    private readonly WavePlayerManager _wavePlayerManager;
    private readonly IndividualSegmentPlayer _individualPlayer;
    private FillerInsertionService _fillerService;
    private SegmentGenerationWaiter _generationWaiter;
    private bool _disposed;

    public AudioSegmentPlayer(AudioSettings settings, AudioFormatDetector formatDetector, FillerManager? fillerManager = null, AudioProcessingChannel? processingChannel = null)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));

        // 各専門クラスのインスタンスを作成
        this._wavePlayerManager = new WavePlayerManager(settings);
        this._individualPlayer = new IndividualSegmentPlayer(settings, formatDetector);
        this._fillerService = new FillerInsertionService(fillerManager);
        this._generationWaiter = new SegmentGenerationWaiter(processingChannel);
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
            throw new ObjectDisposedException(nameof(AudioSegmentPlayer));

        try
        {
            // 共有WavePlayerインスタンスを取得
            var wavePlayer = this._wavePlayerManager.GetOrCreateSharedWavePlayer();
            this._individualPlayer.SetWavePlayer(wavePlayer);

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0) continue;

                await this._individualPlayer.PlaySegmentAsync(segment, cancellationToken: cancellationToken);
            }
        }
        finally
        {
            this.StopAudio();
        }
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
            throw new ObjectDisposedException(nameof(AudioSegmentPlayer));

        Log.Information("PlayAudioSequentiallyWithGenerationAsync 開始 - セグメント数: {SegmentCount}, フィラーマネージャー: {HasFillerManager}",
            segments.Count, fillerManager != null);

        try
        {
            // 共有WavePlayerインスタンスを取得
            var wavePlayer = this._wavePlayerManager.GetOrCreateSharedWavePlayer();
            this._individualPlayer.SetWavePlayer(wavePlayer);

            // フィラーサービスと生成待機サービスを更新
            this._fillerService = new FillerInsertionService(fillerManager);
            this._generationWaiter = new SegmentGenerationWaiter(processingChannel);

            bool isFirstSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                Log.Information("セグメント {SegmentNumber}/{Total} を処理中: \"{Text}\" (キャッシュ済み: {IsCached})",
                    i + 1, segments.Count, segment.Text, segment.IsCached);

                // 未キャッシュセグメントの音声生成を待機
                if (!segment.IsCached || segment.AudioData == null)
                {
                    await this._generationWaiter.WaitForSegmentGenerationAsync(segment, i, cancellationToken);
                }

                // セグメントを再生
                await this._individualPlayer.PlaySegmentAsync(segment.AudioData!, isFirstSegment, cancellationToken);
                isFirstSegment = false;

                // フィラーの挿入をチェック
                var fillerAudio = await this._fillerService.CheckAndGetFillerAsync(i, segments, cancellationToken);
                if (fillerAudio != null)
                {
                    Log.Information("フィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                    await this._individualPlayer.PlaySegmentAsync(fillerAudio, false, cancellationToken);
                }

                // セグメント間の間隔を確保
                if (i < segments.Count - 1)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            Log.Information("全セグメントの再生が完了しました");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayAudioSequentiallyWithGenerationAsync でエラーが発生しました");
            throw;
        }
        finally
        {
            this.StopAudio();
        }
    }



    /// <summary>
    /// 音声再生を停止します
    /// </summary>
    public void StopAudio()
    {
        try
        {
            this._individualPlayer.StopAudio();
            this._wavePlayerManager.StopSharedWavePlayer();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音声停止中にエラーが発生しました");
        }
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this.StopAudio();
                this._individualPlayer?.Dispose();
                this._wavePlayerManager?.Dispose();
                this._disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AudioSegmentPlayerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}
