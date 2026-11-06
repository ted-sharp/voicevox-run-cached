using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

        var data = await mgr.GetRandomFillerAudioAsync();

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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

        var data = await mgr.GetRandomFillerAudioAsync();

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
            var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

            var data = await mgr.GetRandomFillerAudioAsync();

            await Assert.That(data).IsNull();
        }
        finally
        {
            Directory.Delete(dir, true);
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
            var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

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
            var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 }, NullLogger.Instance);

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
            var mgr = new FillerManager(settings, engine.CreateSettings(), NullLogger.Instance);
            using var apiClient = engine.CreateApiClient();

            SynthesisResult result;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = await mgr.InitializeFillerCacheAsync(apiClient);
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
}
