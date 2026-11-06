using Jab;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

/// <summary>
/// Jab (Source Generator) によるコンパイル時 DI コンテナ。
/// 依存の登録漏れは JAB0002、循環依存は JAB0008 としてビルドエラーになる。
/// </summary>
[ServiceProvider]
// 設定 (外部から渡すインスタンス)
[Singleton(typeof(AppSettings), Instance = nameof(Settings))]
[Singleton(typeof(VoiceVoxSettings), Instance = nameof(VoiceVox))]
[Singleton(typeof(CacheSettings), Instance = nameof(Cache))]
[Singleton(typeof(AudioSettings), Instance = nameof(Audio))]
[Singleton(typeof(FillerSettings), Instance = nameof(Filler))]
// ロガー
[Singleton(typeof(ILogger), Instance = nameof(Logger))]
// 時刻プロバイダ (テストで mock 可能にするため抽象化)
[Singleton(typeof(IClock), Instance = nameof(Clock))]
// 基盤サービス
[Singleton(typeof(RetryPolicyService))]
[Singleton(typeof(VoiceVoxApiClient))]
[Singleton(typeof(AudioCacheManager))]
[Singleton(typeof(VoiceVoxEngineManager))]
[Singleton(typeof(TextSegmentProcessor))]
// 音声再生サブシステム (IDisposable はプロバイダの Dispose で破棄される)
[Singleton(typeof(AudioDeviceManager))]
[Singleton(typeof(AudioFormatDetector))]
[Singleton(typeof(WavePlayerManager))]
[Singleton(typeof(AudioPlayer))]
[Singleton(typeof(FillerManager))]
// コマンド処理ヘルパ
[Singleton(typeof(AudioExportService))]
[Singleton(typeof(SegmentProducer))]
[Singleton(typeof(TextToSpeechProcessor))]
// コマンドハンドラ
[Singleton(typeof(SpeakerCommandHandler))]
[Singleton(typeof(DeviceCommandHandler))]
[Singleton(typeof(InitCommandHandler))]
[Singleton(typeof(CacheCommandHandler))]
// ルーター
[Singleton(typeof(CommandRouter))]
public partial class AppServiceProvider
{
    public required AppSettings Settings { get; init; }
    public required ILogger Logger { get; init; }

    public IClock Clock => SystemClock.Instance;
    public VoiceVoxSettings VoiceVox => Settings.VoiceVox;
    public CacheSettings Cache => Settings.Cache;
    public AudioSettings Audio => Settings.Audio;
    public FillerSettings Filler => Settings.Filler;
}
