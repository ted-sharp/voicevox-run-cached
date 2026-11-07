using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class FillerManagerTests
{
    [Test]
    public async Task GetRandomFillerAudio_WhenDisabled_ReturnsNull()
    {
        var settings = new FillerSettings
        {
            Enabled = false,
            Directory = Path.GetTempPath(),
            FillerTexts = ["a", "b"]
        };
        var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

        var data = await mgr.GetRandomFillerAudioAsync(speakerId: 1);

        await Assert.That(data).IsNull();
    }

    [Test]
    public async Task GetRandomFillerAudio_WhenEmptyTexts_ReturnsNull()
    {
        var settings = new FillerSettings
        {
            Enabled = true,
            Directory = Path.GetTempPath(),
            FillerTexts = []
        };
        var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

        var data = await mgr.GetRandomFillerAudioAsync(speakerId: 1);

        await Assert.That(data).IsNull();
    }

    [Test]
    public async Task GetRandomFillerAudio_WhenCacheMiss_ReturnsNull()
    {
        // 存在しないディレクトリにすればキャッシュも無いので null
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["えーっと、"]
            };
            var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

            var data = await mgr.GetRandomFillerAudioAsync(speakerId: 1);

            await Assert.That(data).IsNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task GetRandomFillerAudio_OnlyReturnsFillerOfRequestedSpeaker()
    {
        // 回帰: フィラーは常に DefaultSpeaker の声で、-s 3 で読み上げても途中でフィラーだけ別の声になっていた
        using var engine = new FakeEngineServer();
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FillerSettings { Enabled = true, Directory = dir, FillerTexts = ["a"] };
            var logger = new RecordingLogger();
            var mgr = new FillerManager(settings, new CacheSettings(), logger);
            using var apiClient = engine.CreateApiClient();
            await mgr.InitializeFillerCacheAsync(apiClient, speakerId: 1);

            await Assert.That(await mgr.GetRandomFillerAudioAsync(speakerId: 1)).IsNotNull();
            await Assert.That(await mgr.GetRandomFillerAudioAsync(speakerId: 3)).IsNull();
            await Assert.That(await mgr.GetRandomFillerAudioAsync(speakerId: 3)).IsNull();

            // 未生成の Warning (生成方法の案内付き) は合成待ちのたびではなく 1 回だけ
            await Assert.That(logger.Messages.Count(m => m.Contains("--init -s 3"))).IsEqualTo(1);
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
    public async Task ClearFillerCache_DeletesOpusAndLegacyMp3Files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "x.mp3"), new byte[] { 0xFF });
            await File.WriteAllBytesAsync(Path.Combine(dir, "y.opus"), new byte[] { 0xFF });
            await File.WriteAllTextAsync(Path.Combine(dir, "keep.txt"), "preserved"); // 別拡張子は残す

            var settings = new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["a"]
            };
            var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

            await mgr.ClearFillerCacheAsync();

            await Assert.That(File.Exists(Path.Combine(dir, "x.mp3"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "y.opus"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "keep.txt"))).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ClearFillerCache_NonexistentDirectory_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_nonexistent_" + Guid.NewGuid().ToString("N"));
        var settings = new FillerSettings
        {
            Enabled = true,
            Directory = dir,
            FillerTexts = ["a"]
        };
        var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

        await mgr.ClearFillerCacheAsync();
        // 例外を投げない (no-op で完了)
    }

    [Test]
    public async Task ClearFillerCache_LockedFile_ThrowsCacheException()
    {
        // 削除できないファイルがあるのに --clear が成功表示になっていた回帰の検知
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var lockedPath = Path.Combine(dir, "y.opus");
            await File.WriteAllBytesAsync(lockedPath, new byte[] { 0xFF });
            var settings = new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["a"]
            };
            var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);

            // FileShare.None で開いたままにすると Windows では File.Delete が共有違反 (IOException) になる
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<CacheException>(() => mgr.ClearFillerCacheAsync());
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task InitializeFillerCache_LockedLegacyMp3_StillGeneratesFillers()
    {
        // 回帰: もう読まれない旧形式 .mp3 を削除できないだけで --init が失敗 (終了コード 4) し、フィラーが作られなかった
        using var engine = new FakeEngineServer();
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var lockedPath = Path.Combine(dir, "locked.mp3");
            var unlockedPath = Path.Combine(dir, "unlocked.mp3");
            await File.WriteAllBytesAsync(lockedPath, new byte[] { 0xFF });
            await File.WriteAllBytesAsync(unlockedPath, new byte[] { 0xFF });
            var settings = new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["a"]
            };
            var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);
            using var apiClient = engine.CreateApiClient();

            SynthesisResult result;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = await mgr.InitializeFillerCacheAsync(apiClient, speakerId: 1);
            }

            await Assert.That(result.SuccessCount).IsEqualTo(1);
            await Assert.That(Directory.GetFiles(dir, "*.opus").Length).IsEqualTo(1);
            await Assert.That(File.Exists(unlockedPath)).IsFalse(); // 削除できるものは削除する
            await Assert.That(File.Exists(lockedPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task InitializeFillerCache_EngineUnavailable_StopsGeneratingRemaining()
    {
        // 回帰: --init は読み上げと同じロックを持つのに、エンジンが落ちても全フィラーでリトライ込みの失敗を待ち、
        // その間 Hooks の読み上げを待たせていた
        using var engine = new FakeEngineServer(audioQueryFails: _ => true, audioQueryFailureStatus: HttpStatusCode.ServiceUnavailable);

        var (result, _) = await InitializeAsync(engine, ["a", "b", "c"]);

        // 最初のフィラーのリトライ分だけで、残りには問い合わせない
        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(AudioConstants.RetryMaxAttempts);
        // 中止した分も失敗に数える (全部失敗なら終了コード 5、一部なら 8 のまま)
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 3, SuccessCount: 0, FailureCount: 3));
    }

    [Test]
    public async Task InitializeFillerCache_RequestError_ContinuesWithRemaining()
    {
        // 400 などエンジンが応答している失敗はテキストごとの問題なので、残りの生成は続ける
        using var engine = new FakeEngineServer(audioQueryFails: _ => true, audioQueryFailureStatus: HttpStatusCode.BadRequest);

        var (result, _) = await InitializeAsync(engine, ["a", "b", "c"]);

        await Assert.That(engine.AudioQueryRequestCount).IsEqualTo(3);
        await Assert.That(result).IsEqualTo(new SynthesisResult(AttemptedCount: 3, SuccessCount: 0, FailureCount: 3));
    }

    [Test]
    public async Task InitializeFillerCache_WhenDisabled_StillGenerates()
    {
        // 回帰: Filler.Enabled=false だと --init が何も生成せずに成功表示していたため、
        // appsettings.json・README の「--init で生成してから true にする」手順ではフィラーが作られなかった
        using var engine = new FakeEngineServer();

        var (result, files) = await InitializeAsync(engine, ["a"], enabled: false);

        await Assert.That(result.SuccessCount).IsEqualTo(1);
        await Assert.That(files.Count(f => f.EndsWith(".opus", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task InitializeFillerCache_LeavesOnlyOpusFiles()
    {
        // 一時ファイル (.tmp) に書いてから置き換えるので、生成後は .opus だけが残る
        using var engine = new FakeEngineServer();

        var (result, files) = await InitializeAsync(engine, ["a", "b"]);

        await Assert.That(result.SuccessCount).IsEqualTo(2);
        await Assert.That(files.Length).IsEqualTo(2);
        await Assert.That(files.All(f => f.EndsWith(".opus", StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// 一時ディレクトリに <paramref name="fillerTexts"/> のフィラーを生成し、結果と生成後のファイル (フルパス) を返す。
    /// </summary>
    private static async Task<(SynthesisResult Result, string[] Files)> InitializeAsync(
        FakeEngineServer engine, string[] fillerTexts, bool enabled = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FillerSettings { Enabled = enabled, Directory = dir, FillerTexts = fillerTexts };
            var mgr = new FillerManager(settings, new CacheSettings(), NullLogger.Instance);
            using var apiClient = engine.CreateApiClient();

            var result = await mgr.InitializeFillerCacheAsync(apiClient, FakeEngineServer.SpeakerId);
            return (result, Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
