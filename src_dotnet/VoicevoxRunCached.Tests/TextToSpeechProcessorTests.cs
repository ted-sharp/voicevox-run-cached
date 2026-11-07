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

    [Test]
    public async Task Refresh_ResynthesizesAndOverwritesCache()
    {
        // 辞書登録やエンジン更新を反映するため、キャッシュ済みでも合成し直してキャッシュを上書きする
        using var engine = new FakeEngineServer();

        var run = await RunAsync(engine.BaseUrl, [CachedText, "--refresh", "--no-play"]);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(run.CacheAfter.SequenceEqual(run.CacheBefore)).IsFalse();
    }

    [Test]
    public async Task NoCache_ResynthesizesWithoutOverwritingCache()
    {
        // --no-cache は読みも書きもしない (作り直しは --refresh)
        using var engine = new FakeEngineServer();

        var run = await RunAsync(engine.BaseUrl, [CachedText, "--no-cache", "--no-play"]);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(run.CacheAfter.SequenceEqual(run.CacheBefore)).IsTrue();
    }

    /// <summary>
    /// 待ち受けていないポートを BaseUrl にし (自動起動なし)、<see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    private static async Task<int> RunWithStoppedEngineAsync(string[] args)
        => (await RunAsync($"http://127.0.0.1:{FakeEngineServer.GetFreePort()}", args)).ExitCode;

    /// <summary>
    /// <paramref name="baseUrl"/> のエンジンに向け (自動起動なし)、<see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    /// <returns>終了コードと、実行前後の <see cref="CachedText"/> のキャッシュファイルの中身</returns>
    private static async Task<(int ExitCode, byte[] CacheBefore, byte[] CacheAfter)> RunAsync(string baseUrl, string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_tts_" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "audio");
        var settings = new AppSettings
        {
            VoiceVox = new VoiceVoxSettings
            {
                BaseUrl = baseUrl,
                AutoStartEngine = false,
                DefaultSpeaker = 1,
            },
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = cacheDir, UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };

        try
        {
            using var provider = new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance };

            // ArgumentParser の既定値 (DefaultSpeaker, speed 1.0, pitch 0.0, volume 1.0) と同じキーで保存する
            var cachedRequest = new VoiceRequest(CachedText, 1);
            await provider.GetService<AudioCacheManager>()
                .SaveAudioCacheAsync(cachedRequest, TestAudio.SineWav(24000, 300));
            var cachePath = Path.Combine(cacheDir, CacheKeyGenerator.Compute(cachedRequest) + DiskCacheStore.AudioExtension);
            var before = await File.ReadAllBytesAsync(cachePath);

            var exitCode = await provider.GetService<CommandRouter>().ExecuteCommandAsync(args);

            return (exitCode, before, await File.ReadAllBytesAsync(cachePath));
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
