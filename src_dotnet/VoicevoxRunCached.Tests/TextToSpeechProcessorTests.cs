using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Infrastructure;

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
    public async Task OutPathNotWritable_FailsBeforeSynthesis()
    {
        // 回帰: --out に書き込めないことが合成・再生の後で分かっていた。合成の前 (ロックを待つ前) にエラーにする
        // (合成していればエンジン停止のエラー (終了コード 3) になる)
        var file = Path.Combine(Path.GetTempPath(), $"vrc_out_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, "x");
        try
        {
            var exitCode = await RunWithStoppedEngineAsync(["キャッシュに無い文です。", "--no-play", "--out", Path.Combine(file, "out.wav")]);

            await Assert.That(exitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Audio.AudioFileExportFailed));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task NoPlay_UpdatesFillerLastUsed()
    {
        // 回帰: フィラーの最終利用日時を再生が正常に終わった後にだけ更新していたため、--no-play (Ctrl+C・再生の失敗も) では更新されず、
        // 使っている話者のフィラーも --init で消えていた
        using var stoppedEngine = new StoppedEngine();
        var old = DateTime.UtcNow.AddDays(-40);
        string? fillerPath = null;
        var lastUsed = DateTime.MinValue;

        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = stoppedEngine.BaseUrl, AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText, "--no-play"],
            afterCommand: () =>
            {
                lastUsed = File.GetLastWriteTimeUtc(fillerPath!);
                return Task.CompletedTask;
            },
            prepareCache: async cachePath =>
            {
                var fillerDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cachePath))!, "filler");
                Directory.CreateDirectory(fillerDir);
                fillerPath = Path.Combine(fillerDir, CacheKeyGenerator.Compute(new VoiceRequest("えーと", "1")) + DiskCacheStore.AudioExtension);
                await File.WriteAllBytesAsync(fillerPath, [1]);
                File.SetLastWriteTimeUtc(fillerPath, old);
            },
            fillerTexts: ["えーと"]);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(lastUsed).IsGreaterThan(old.AddDays(39));
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

        using var stoppedEngine = new StoppedEngine();
        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = stoppedEngine.BaseUrl, AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText + otherText, "--cache-only", "--no-play"],
            prepareCache: path => File.WriteAllBytesAsync(path, [1, 2, 3, 4]),
            alsoCached: [otherText]);

        await Assert.That(run.ExitCode).IsEqualTo(ErrorCodes.PartialFailureExitCode);
    }

    [Test]
    public async Task CorruptedCache_CacheOnly_IsTotalFailure()
    {
        using var stoppedEngine = new StoppedEngine();
        var run = await RunAsync(
            new VoiceVoxSettings { BaseUrl = stoppedEngine.BaseUrl, AutoStartEngine = false, DefaultSpeaker = 1 },
            [CachedText, "--cache-only", "--no-play"],
            prepareCache: path => File.WriteAllBytesAsync(path, [1, 2, 3, 4]));

        await Assert.That(run.ExitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Audio.AudioGenerationFailed));
    }

    [Test]
    public async Task WaitingForPlaybackDevice_ReleasesSpeechLock()
    {
        // 合成を終えたら、再生 (再生デバイスの順番待ちも) の途中でも SpeechLock を手放す。
        // 手放さないと、違うデバイスへの読み上げも先の再生が終わるまで待たされ、同時に鳴らせない
        await RunWhilePlaybackDeviceBusyAsync([], () => Task.CompletedTask);
    }

    [Test]
    public async Task OutFile_IsSavedBeforeReleasingSpeechLock()
    {
        // 回帰: --out の保存を SpeechLock の外 (再生の後) で行うと、同じ --out の実行どうしが同時に {out}.tmp に書き、
        // 片方が失敗したり、もう一方の一時ファイルを消したりする。SpeechLock が空いた時点で保存が済んでいることを確かめる
        var outPath = Path.Combine(Path.GetTempPath(), $"vrc_out_{Guid.NewGuid():N}.wav");
        try
        {
            await RunWhilePlaybackDeviceBusyAsync(["--out", outPath], async () => await Assert.That(File.Exists(outPath)).IsTrue());
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    /// <summary>
    /// OS 既定のデバイスで先の読み上げが再生中のときに、キャッシュ済みの文を読み上げる。再生の順番を待っている間に SpeechLock を取り、
    /// <paramref name="whileSpeechLockHeld"/> を呼ぶ。最後にキャンセルして、音は鳴らさない。
    /// </summary>
    private static async Task RunWhilePlaybackDeviceBusyAsync(string[] extraArgs, Func<Task> whileSpeechLockHeld)
    {
        using var stoppedEngine = new StoppedEngine();
        using var lockFile = new TestLockFile();
        using var lockDirectory = new TempLockDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "vrc_tts_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            VoiceVox = new VoiceVoxSettings { BaseUrl = stoppedEngine.BaseUrl, AutoStartEngine = false, DefaultSpeaker = 1 },
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = Path.Combine(dir, "audio"), UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };
        var logger = new RecordingLogger();
        try
        {
            using var provider = new AppServiceProvider
            {
                Settings = settings,
                Logger = logger,
                SpeechLockPath = lockFile.Path,
                PlaybackLockDirectory = lockDirectory.Path,
            };
            await provider.GetService<AudioCacheManager>().SaveAudioCacheAsync(new VoiceRequest(CachedText, "1"), TestAudio.SineWav(24000, 300));

            // OS 既定のデバイスは先の読み上げが再生中 (このテストでは音を鳴らさずに終わらせる。番号はこの PC にあるものしか指定できない)
            using var playing = await lockDirectory.CreatePlaybackLock().AcquireAsync(-1, maxWait: null, TimeSpan.Zero, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var speaking = provider.GetService<CommandRouter>().ExecuteCommandAsync([CachedText, "--device", "-1", .. extraArgs], cts.Token);

            bool speechLockFree;
            try
            {
                var waitingForDevice = Task.Run(async () =>
                {
                    while (!logger.Messages.Any(m => m.Contains("VoicevoxRunCached.playback.default.lock", StringComparison.Ordinal)))
                    {
                        await Task.Delay(20);
                    }
                });
                await waitingForDevice.WaitAsync(TimeSpan.FromSeconds(10));

                // 再生を待ち始めた時点では、まだ producer が終わっていない (SpeechLock を持っている) ことがあるので少し待つ。
                // 手放さない作りなら、再生のロックが空くまで (ここではキャンセルするまで) 取れない
                using var speechLock = await InterProcessLock.AcquireAsync(lockFile.Path, NullLogger.Instance, TimeSpan.FromSeconds(5));
                speechLockFree = true;
                await whileSpeechLockHeld();
            }
            catch (VoicevoxRunCachedException ex) when (ex.ErrorCode == ErrorCodes.Lock.LockWaitTimeout)
            {
                speechLockFree = false;
            }
            finally
            {
                // 音を鳴らさずに終わらせる (ロックファイルを消す前に)
                await cts.CancelAsync();
            }

            await Assert.That(speechLockFree).IsTrue();
            await Assert.That(await speaking.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(ErrorCodes.CancelledExitCode);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
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
    {
        using var stoppedEngine = new StoppedEngine();
        return (await RunAsync(stoppedEngine.BaseUrl, args)).ExitCode;
    }

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
    /// <param name="fillerTexts">指定したらフィラーを有効にし、その文にする (フィラーのフォルダはキャッシュのフォルダと並びの <c>filler</c>)</param>
    /// <returns>終了コードと、実行前後の <see cref="CachedText"/> のキャッシュファイルの中身 (実行後に無ければ空)</returns>
    private static async Task<(int ExitCode, byte[] CacheBefore, byte[] CacheAfter)> RunAsync(
        VoiceVoxSettings voiceVox, string[] args, Func<Task>? afterCommand = null, Func<string, Task>? prepareCache = null,
        string[]? alsoCached = null, string[]? fillerTexts = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_tts_" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(dir, "audio");
        using var lockFile = new TestLockFile();
        var settings = new AppSettings
        {
            VoiceVox = voiceVox,
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = cacheDir, UseExecutableBaseDirectory = false },
            Filler = new FillerSettings
            {
                Enabled = fillerTexts != null,
                Directory = Path.Combine(dir, "filler"),
                UseExecutableBaseDirectory = false,
                FillerTexts = fillerTexts ?? [],
            },
        };

        try
        {
            using var provider = new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance, SpeechLockPath = lockFile.Path };

            // ArgumentParser の既定値 (DefaultSpeaker, speed 1.0, pitch 0.0, volume 1.0) と同じキーで保存する
            var cachedRequest = new VoiceRequest(CachedText, "1");
            var cacheManager = provider.GetService<AudioCacheManager>();
            await cacheManager.SaveAudioCacheAsync(cachedRequest, TestAudio.SineWav(24000, 300));
            foreach (var text in alsoCached ?? [])
            {
                await cacheManager.SaveAudioCacheAsync(new VoiceRequest(text, "1"), TestAudio.SineWav(24000, 300));
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
