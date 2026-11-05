namespace VoicevoxRunCached.Constants;

/// <summary>
/// 音声処理関連の定数定義
/// </summary>
public static class AudioConstants
{
    /// <summary>
    /// 音声再生タイムアウトの余裕時間（ミリ秒）。実際のタイムアウトは「音声の長さ + この値」
    /// (長文 × 低速再生でも正常再生をタイムアウト扱いしないため)
    /// </summary>
    public const int PlaybackTimeoutMarginMs = 30000;

    /// <summary>
    /// 最初のセグメント再生前の遅延時間（ミリ秒）
    /// </summary>
    public const int FirstSegmentDelayMs = 200;

    /// <summary>
    /// 後続セグメント再生前の遅延時間（ミリ秒）
    /// </summary>
    public const int SubsequentSegmentDelayMs = 20;

    /// <summary>
    /// ストリーミング再生で次のセグメントが届かないままこの時間を超えたらフィラー音声を挿入する（ミリ秒）。
    /// </summary>
    public const int FillerStartDelayMs = 800;

    /// <summary>
    /// 最初のセグメント再生完了後の遅延時間（ミリ秒）
    /// </summary>
    public const int FirstSegmentTrailingDelayMs = 150;

    /// <summary>
    /// 後続セグメント再生完了後の遅延時間（ミリ秒）
    /// </summary>
    public const int SubsequentSegmentTrailingDelayMs = 100;

    /// <summary>
    /// Opus エンコード時の既定ビットレート (kbps, VBR)。音声合成の 24kHz mono なら 32kbps で MP3 64kbps 相当以上。
    /// キャッシュ保存分は <c>Cache.OpusBitrateKbps</c> 設定で上書きできる。
    /// </summary>
    public const int OpusBitrateKbps = 32;

    /// <summary>
    /// <c>Cache.OpusBitrateKbps</c> で許可する範囲 (kbps)。
    /// </summary>
    public const int MinOpusBitrateKbps = 12;
    public const int MaxOpusBitrateKbps = 128;

    /// <summary>
    /// Opus デコード時の出力サンプルレート。Concentus は 24kHz デコードで無音化する問題があるため 48kHz 固定。
    /// </summary>
    public const int OpusDecodeSampleRate = 48000;

    /// <summary>
    /// Opus エンコーダが入力として受け付けるサンプルレート。これ以外は 48kHz にリサンプルしてからエンコードする。
    /// </summary>
    public static readonly int[] OpusSupportedSampleRates = [8000, 12000, 16000, 24000, 48000];

    /// <summary>
    /// デバイス準備のタイムアウト時間（秒）
    /// </summary>
    public const int DevicePreparationTimeoutSeconds = 5;

    /// <summary>
    /// デバイス準備クリーンアップのタイムアウト時間（秒）
    /// </summary>
    public const int DevicePreparationCleanupTimeoutSeconds = 2;

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
    /// デバイス warm-up 用の sine wave 周波数（Hz）
    /// </summary>
    public const double WarmupFrequencyHz = 440.0;

    /// <summary>
    /// デバイス warm-up 用 sine wave の低音量レベル
    /// </summary>
    public const short WarmupAmplitude = 32;

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
    /// ProgressSpinner のアニメーション停止待機時間（ミリ秒）
    /// </summary>
    public const int ProgressSpinnerStopWaitTimeoutMs = 1000;

    /// <summary>
    /// API リトライの最大試行回数
    /// </summary>
    public const int RetryMaxAttempts = 3;

    /// <summary>
    /// API リトライの初回バックオフ遅延（ミリ秒）
    /// </summary>
    public const int RetryBaseDelayMs = 1000;

    /// <summary>
    /// API リトライのバックオフ上限（ミリ秒）
    /// </summary>
    public const int RetryMaxDelayMs = 30000;
}
