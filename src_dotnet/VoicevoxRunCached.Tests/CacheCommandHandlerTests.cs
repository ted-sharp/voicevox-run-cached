using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Commands;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class CacheCommandHandlerTests
{
    [Test]
    public async Task Clear_WaitsForSpeechLock()
    {
        // 回帰: --clear がプロセス間ロックを取らず、読み上げのキャッシュ保存 (meta.json → .opus) と並行して走っていた。
        // 間に消すと meta.json の無い .opus が残って期限切れの掃除で消えず、書き込み中のファイルに当たると --clear が失敗する
        var dir = Path.Combine(Path.GetTempPath(), "vrc_clear_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cacheSettings = new CacheSettings { Directory = Path.Combine(dir, "audio"), UseExecutableBaseDirectory = false };
            var fillerSettings = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false };
            using var engineManager = new VoiceVoxEngineManager(new VoiceVoxSettings(), NullLogger.Instance, SystemClock.Instance);
            var handler = new CacheCommandHandler(
                new AudioCacheManager(cacheSettings, NullLogger.Instance, SystemClock.Instance),
                new FillerManager(fillerSettings, cacheSettings, NullLogger.Instance),
                new SpeechLock(engineManager, NullLogger.Instance),
                NullLogger.Instance);

            Task<int> clear;
            using (await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, NullLogger.Instance))
            {
                clear = handler.HandleClearCacheAsync();
                await Task.Delay(300);
                await Assert.That(clear.IsCompleted).IsFalse();
            }

            await Assert.That(await clear.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);
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
