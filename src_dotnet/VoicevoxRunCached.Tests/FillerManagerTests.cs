using VoicevoxRunCached.Configuration;
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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 });

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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 });

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
            var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 });

            var data = await mgr.GetRandomFillerAudioAsync();

            await Assert.That(data).IsNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ClearFillerCache_DeletesMp3Files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_filler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "x.mp3"), new byte[] { 0xFF });
            await File.WriteAllTextAsync(Path.Combine(dir, "keep.txt"), "preserved"); // 別拡張子は残す

            var settings = new FillerSettings
            {
                Enabled = true,
                Directory = dir,
                FillerTexts = ["a"]
            };
            var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 });

            mgr.ClearFillerCache();

            await Assert.That(File.Exists(Path.Combine(dir, "x.mp3"))).IsFalse();
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
        var mgr = new FillerManager(settings, new VoiceVoxSettings { DefaultSpeaker = 1 });

        mgr.ClearFillerCache();
        // 例外を投げない (no-op で完了)
    }
}
