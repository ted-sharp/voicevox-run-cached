using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 読み上げコマンドの end-to-end (CommandRouter 経由、再生はしない)。エンジンは止まっている前提。
/// </summary>
public class TextToSpeechProcessorTests
{
    private const string CachedText = "キャッシュ済みの文です。";

    [Test]
    public async Task AllCached_CacheOnly_DoesNotNeedEngine()
    {
        // 回帰: 最初にエンジンの確認をしていたため、全部キャッシュ済みでもエンジン停止中は終了コード 3 になり
        // (AutoStartEngine=true なら起動を待たされ)、--cache-only の「API を呼ばない」が成り立っていなかった
        var exitCode = await RunWithStoppedEngineAsync([CachedText, "--cache-only", "--no-play"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task AllCached_DoesNotNeedEngine()
    {
        var exitCode = await RunWithStoppedEngineAsync([CachedText, "--no-play"]);

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task NotCached_CacheOnly_ReturnsCacheError()
    {
        var exitCode = await RunWithStoppedEngineAsync(["キャッシュに無い文です。", "--cache-only", "--no-play"]);

        await Assert.That(exitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Cache.CacheReadError));
    }

    [Test]
    public async Task NotCached_NeedsEngine_ReturnsEngineError()
    {
        var exitCode = await RunWithStoppedEngineAsync(["キャッシュに無い文です。", "--no-play"]);

        await Assert.That(exitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Engine.EngineNotAvailable));
    }

    /// <summary>
    /// 待ち受けていないポートを BaseUrl にし (自動起動なし)、<see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    private static async Task<int> RunWithStoppedEngineAsync(string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_tts_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            VoiceVox = new VoiceVoxSettings
            {
                BaseUrl = $"http://127.0.0.1:{FakeEngineServer.GetFreePort()}",
                AutoStartEngine = false,
                DefaultSpeaker = 1,
            },
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = Path.Combine(dir, "audio"), UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };

        try
        {
            using var provider = new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance };

            // ArgumentParser の既定値 (DefaultSpeaker, speed 1.0, pitch 0.0, volume 1.0) と同じキーで保存する
            await provider.GetService<AudioCacheManager>()
                .SaveAudioCacheAsync(new VoiceRequest(CachedText, 1), TestAudio.SineWav(24000, 300));

            return await provider.GetService<CommandRouter>().ExecuteCommandAsync(args);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
