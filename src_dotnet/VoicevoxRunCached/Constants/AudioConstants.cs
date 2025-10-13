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
}
