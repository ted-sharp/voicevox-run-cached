namespace VoicevoxRunCached.Constants;

/// <summary>
/// 音声処理関連の定数定義
/// </summary>
public static class AudioConstants
{
    /// <summary>
    /// デフォルトの音声再生タイムアウト時間（ミリ秒）
    /// </summary>
    public const int DefaultPlaybackTimeoutMs = 30000;

    /// <summary>
    /// 最初のセグメント再生前の遅延時間（ミリ秒）
    /// </summary>
    public const int FirstSegmentDelayMs = 200;

    /// <summary>
    /// 後続セグメント再生前の遅延時間（ミリ秒）
    /// </summary>
    public const int SubsequentSegmentDelayMs = 20;

    /// <summary>
    /// バッファフラッシュのための遅延時間（ミリ秒）
    /// </summary>
    public const int BufferFlushDelayMs = 150;

    /// <summary>
    /// セグメント間の挿入待機時間（ミリ秒）
    /// </summary>
    public const int InterSegmentGapMs = 50;

    /// <summary>
    /// 最初のセグメント再生完了後の遅延時間（ミリ秒）
    /// </summary>
    public const int FirstSegmentTrailingDelayMs = 150;

    /// <summary>
    /// 後続セグメント再生完了後の遅延時間（ミリ秒）
    /// </summary>
    public const int SubsequentSegmentTrailingDelayMs = 100;

    /// <summary>
    /// FFmpeg変換のタイムアウト時間（ミリ秒）
    /// </summary>
    public const int FfmpegTimeoutMs = 30000;

    /// <summary>
    /// デバイス準備のタイムアウト時間（秒）
    /// </summary>
    public const int DevicePreparationTimeoutSeconds = 5;

    /// <summary>
    /// デバイス準備クリーンアップのタイムアウト時間（秒）
    /// </summary>
    public const int DevicePreparationCleanupTimeoutSeconds = 2;

    /// <summary>
    /// バックグラウンドキャッシュクリーンアップのタイムアウト時間（ミリ秒）
    /// </summary>
    public const int BackgroundCacheCleanupTimeoutMs = 5000;

    /// <summary>
    /// デフォルトサンプルレート
    /// </summary>
    public const int DefaultSampleRate = 22050;

    /// <summary>
    /// デフォルトチャンネル数
    /// </summary>
    public const int DefaultChannels = 1;

    /// <summary>
    /// デフォルトビット深度
    /// </summary>
    public const int DefaultBitsPerSample = 16;

    /// <summary>
    /// フィラー音声生成用の周波数（Hz）
    /// </summary>
    public const double FillerFrequencyHz = 440.0;

    /// <summary>
    /// フィラー音声生成用の低音量レベル
    /// </summary>
    public const short FillerAmplitude = 32;

    /// <summary>
    /// VOICEVOX エンジンプロセス停止時の待機時間（ミリ秒）
    /// </summary>
    public const int EngineStopWaitTimeoutMs = 5000;

    /// <summary>
    /// VOICEVOX エンジン死活監視 (HTTP /version) のタイムアウト時間（秒）
    /// </summary>
    public const int EngineHealthCheckTimeoutSeconds = 5;

    /// <summary>
    /// VOICEVOX エンジン起動待機時のポーリング間隔（ミリ秒）
    /// </summary>
    public const int EngineStartupPollIntervalMs = 1000;

    /// <summary>
    /// AudioCacheManager のバックグラウンドサイズ eviction の最大実行時間（分）
    /// </summary>
    public const int CacheBackgroundEvictionTimeoutMinutes = 5;

    /// <summary>
    /// ProgressSpinner のアニメーション停止待機時間（ミリ秒）
    /// </summary>
    public const int ProgressSpinnerStopWaitTimeoutMs = 1000;

    /// <summary>
    /// Polly サーキットブレーカーの遮断継続時間（秒）
    /// </summary>
    public const int CircuitBreakerDurationSeconds = 30;

    /// <summary>
    /// Polly タイムアウトポリシーの 1 操作あたりの上限時間（秒）
    /// </summary>
    public const int OperationTimeoutSeconds = 30;

    /// <summary>
    /// Polly サーキットブレーカーが開く前に許容する例外回数
    /// </summary>
    public const int CircuitBreakerExceptionThreshold = 3;
}
