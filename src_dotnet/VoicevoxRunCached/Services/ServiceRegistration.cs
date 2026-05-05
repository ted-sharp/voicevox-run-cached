using DryIoc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Commands;

namespace VoicevoxRunCached.Services;

/// <summary>
/// DryIoc コンテナへのサービス登録を行う
/// </summary>
public static class ServiceRegistration
{
    public static Container Build(AppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        var container = new Container();

        // 設定 (singleton)
        container.RegisterInstance(settings);
        container.RegisterInstance(settings.VoiceVox);
        container.RegisterInstance(settings.Cache);
        container.RegisterInstance(settings.Audio);
        container.RegisterInstance(settings.Filler);
        container.RegisterInstance(settings.Logging);
        container.RegisterInstance(settings.Test);

        // ロガー (singleton)
        container.RegisterInstance(logger);

        // 時刻プロバイダ (テストで mock 可能にするため抽象化)
        container.RegisterInstance<IClock>(SystemClock.Instance);

        // MemoryCache は IMemoryCache として登録 (CacheSettings.MemoryCacheSizeMb をサイズ上限に)
        var memoryCacheOptions = new MemoryCacheOptions
        {
            SizeLimit = settings.Cache.MemoryCacheSizeMb * 1024L * 1024L
        };
        container.RegisterInstance<IMemoryCache>(new MemoryCache(memoryCacheOptions));

        // 基盤サービス (singleton)
        container.Register<RetryPolicyService>(Reuse.Singleton);
        container.Register<VoiceVoxApiClient>(Reuse.Singleton);
        container.Register<AudioCacheManager>(Reuse.Singleton);
        container.Register<VoiceVoxEngineManager>(Reuse.Singleton);
        container.Register<TextSegmentProcessor>(Reuse.Singleton);

        // 音声再生サブシステム (singleton, IDisposable は DryIoc が破棄)
        container.Register<AudioDeviceManager>(Reuse.Singleton);
        container.Register<AudioFormatDetector>(Reuse.Singleton);
        container.Register<WavePlayerManager>(Reuse.Singleton);
        container.Register<AudioPlayer>(Reuse.Singleton);

        // FillerManager は (FillerSettings, int defaultSpeakerId, ILogger) を取るので明示的にファクトリ登録
        container.RegisterDelegate<FillerManager>(
            r => new FillerManager(r.Resolve<FillerSettings>(), settings.VoiceVox.DefaultSpeaker, r.Resolve<Microsoft.Extensions.Logging.ILogger>()),
            Reuse.Singleton);

        // コマンド処理ヘルパ (singleton)
        container.Register<AudioExportService>(Reuse.Singleton);
        container.Register<SegmentProducer>(Reuse.Singleton);
        container.Register<TextToSpeechProcessor>(Reuse.Singleton);

        // コマンドハンドラ (singleton)
        container.Register<SpeakerCommandHandler>(Reuse.Singleton);
        container.Register<DeviceCommandHandler>(Reuse.Singleton);
        container.Register<InitCommandHandler>(Reuse.Singleton);
        container.Register<CacheCommandHandler>(Reuse.Singleton);
        container.Register<BenchmarkCommandHandler>(Reuse.Singleton);

        // ルーター (singleton)
        container.Register<CommandRouter>(Reuse.Singleton);

        return container;
    }
}
