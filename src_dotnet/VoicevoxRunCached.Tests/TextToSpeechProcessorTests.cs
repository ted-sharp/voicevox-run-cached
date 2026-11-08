using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Services.Audio;
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
    public async Task PartlyCached_NeedsEngine_ReturnsEngineError()
    {
        // 先頭のキャッシュ済みの文は鳴らし (再生を途中で止めない)、終了コードは準備の失敗 (エンジン無し) のまま
        var exitCode = await RunWithStoppedEngineAsync([CachedText + "キャッシュに無い文です。", "--no-play"]);

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

    [Test]
    public async Task CorruptedCache_IsResynthesizedAndRewritten()
    {
        // 壊れた .opus は読み込み時にミスになり、その場で合成し直して保存し直す (今回の読み上げも成功する)
        using var engine = new FakeEngineServer();
        byte[] garbage = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251))];

        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = engine.BaseUrl, AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText, "--no-play"],
            prepareCache: path => File.WriteAllBytesAsync(path, garbage));

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(1);
        await Assert.That(run.CacheBefore.SequenceEqual(garbage)).IsTrue();
        await Assert.That(AudioFormatDetector.DetectFormat(run.CacheAfter)).IsEqualTo(AudioFormat.Opus);
    }

    [Test]
    public async Task CorruptedCache_CacheOnly_WithOtherCachedText_IsPartialFailure()
    {
        // 回帰: キャッシュから鳴った文を数えず、合成を試みた文 (読めなかった 1 文) が全部失敗したことで「全部失敗」(5) にしていた
        const string otherText = "もう一つの文です。";

        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = $"http://127.0.0.1:{FakeEngineServer.GetFreePort()}", AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText + otherText, "--cache-only", "--no-play"],
            prepareCache: path => File.WriteAllBytesAsync(path, [1, 2, 3, 4]),
            alsoCached: [otherText]);

        await Assert.That(run.ExitCode).IsEqualTo(ErrorCodes.PartialFailureExitCode);
    }

    [Test]
    public async Task CorruptedCache_CacheOnly_IsTotalFailure()
    {
        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = $"http://127.0.0.1:{FakeEngineServer.GetFreePort()}", AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText, "--cache-only", "--no-play"],
            prepareCache: path => File.WriteAllBytesAsync(path, [1, 2, 3, 4]));

        await Assert.That(run.ExitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Audio.AudioGenerationFailed));
    }

    [Test]
    public async Task EngineStartedByUs_IsStoppedBeforeReleasingLock()
    {
        // 回帰: KeepEngineRunning=false で自分が起動したエンジンを、プロセス間ロックを解放した後 (サービスの Dispose) に止めていた。
        // その間にロックを取った後続の読み上げが、動いているエンジンを見て合成を始め、途中で止められて失敗する。
        // コマンドから戻った時点 (= ロック解放後、Dispose 前) で止まっていることを確かめる
        using var engineProcess = new FakeEngineProcess();
        using var engine = new FakeEngineServer(versionAvailable: () => engineProcess.HasStarted);
        var stoppedBeforeDispose = false;

        var run = await RunAsync(
            engineProcess.CreateSettings(engine.BaseUrl),
            ["キャッシュに無い文です。", "--no-play"],
            afterCommand: async () => stoppedBeforeDispose = await engineProcess.HasStoppedWithinAsync(TimeSpan.FromSeconds(3)));

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(stoppedBeforeDispose).IsTrue();
    }

    /// <summary>
    /// 待ち受けていないポートを BaseUrl にし (自動起動なし)、<see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    private static async Task<int> RunWithStoppedEngineAsync(string[] args)
        => (await RunAsync($"http://127.0.0.1:{FakeEngineServer.GetFreePort()}", args)).ExitCode;

    /// <summary>
    /// <paramref name="baseUrl"/> のエンジンに向け (自動起動なし)、<see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    private static Task<(int ExitCode, byte[] CacheBefore, byte[] CacheAfter)> RunAsync(string baseUrl, string[] args)
        => RunAsync(new VoiceVoxSettings { BaseUrl = baseUrl, AutoStartEngine = false, DefaultSpeaker = 1 }, args);

    /// <summary>
    /// <see cref="CachedText"/> だけキャッシュした状態でコマンドを実行する。
    /// </summary>
    /// <param name="voiceVox">エンジンの設定</param>
    /// <param name="args">コマンドライン引数</param>
    /// <param name="afterCommand">コマンドから戻った後、サービスを破棄する前に呼ぶ</param>
    /// <param name="prepareCache">キャッシュを保存した後、コマンドの前に呼ぶ (引数は <see cref="CachedText"/> の .opus のパス)</param>
    /// <param name="alsoCached"><see cref="CachedText"/> のほかにキャッシュしておく文</param>
    /// <returns>終了コードと、実行前後の <see cref="CachedText"/> のキャッシュファイルの中身 (実行後に無ければ空)</returns>
    private static async Task<(int ExitCode, byte[] CacheBefore, byte[] CacheAfter)> RunAsync(
        VoiceVoxSettings voiceVox, string[] args, Func<Task>? afterCommand = null, Func<string, Task>? prepareCache = null,
        string[]? alsoCached = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_tts_" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "audio");
        var settings = new AppSettings
        {
            VoiceVox = voiceVox,
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = cacheDir, UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };

        try
        {
            using var provider = new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance };

            // ArgumentParser の既定値 (DefaultSpeaker, speed 1.0, pitch 0.0, volume 1.0) と同じキーで保存する
            var cachedRequest = new VoiceRequest(CachedText, 1);
            var cacheManager = provider.GetService<AudioCacheManager>();
            await cacheManager.SaveAudioCacheAsync(cachedRequest, TestAudio.SineWav(24000, 300));
            foreach (var text in alsoCached ?? [])
            {
                await cacheManager.SaveAudioCacheAsync(new VoiceRequest(text, 1), TestAudio.SineWav(24000, 300));
            }
            var cachePath = Path.Combine(cacheDir, CacheKeyGenerator.Compute(cachedRequest) + DiskCacheStore.AudioExtension);
            if (prepareCache != null)
            {
                await prepareCache(cachePath);
            }
            var before = await File.ReadAllBytesAsync(cachePath);

            var exitCode = await provider.GetService<CommandRouter>().ExecuteCommandAsync(args);
            if (afterCommand != null)
            {
                await afterCommand();
            }

            // 読めないキャッシュは削除される (--cache-only では作り直さない)
            var after = File.Exists(cachePath) ? await File.ReadAllBytesAsync(cachePath) : [];
            return (exitCode, before, after);
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
