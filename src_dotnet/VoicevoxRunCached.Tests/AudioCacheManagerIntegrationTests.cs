using Microsoft.Extensions.Caching.Memory;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

public class AudioCacheManagerIntegrationTests
{
    private static (AudioCacheManager manager, string dir) CreateManager(double maxSizeGb = 1.0)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_acm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new CacheSettings
        {
            Directory = dir,
            ExpirationDays = 30,
            MaxSizeGb = maxSizeGb,
            MemoryCacheSizeMb = 100,
            UseExecutableBaseDirectory = false
        };
        var memCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        return (new AudioCacheManager(settings, memCache), dir);
    }

    [Test]
    public async Task SaveAudioCache_DoesNotEvict_RunMaintenanceDoes()
    {
        // 上限 ~107B。保存ごとの全走査をやめたので、Save 単体では上限超過でも消えず、
        // RunMaintenanceAsync で初めて古い順に削除される
        var (mgr, dir) = CreateManager(maxSizeGb: 1e-7);
        try
        {
            var wav = AudioConversionUtility.CreateMinimalWavData(300);
            await mgr.SaveAudioCacheAsync(new VoiceRequest("一つ目", 1), wav);
            await mgr.SaveAudioCacheAsync(new VoiceRequest("二つ目", 1), wav);

            await Assert.That(Directory.GetFiles(dir, "*.mp3").Length).IsEqualTo(2);

            await mgr.RunMaintenanceAsync();

            await Assert.That(Directory.GetFiles(dir, "*.mp3").Length).IsLessThan(2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task GetCachedAudio_OnMiss_ReturnsNull()
    {
        var (mgr, dir) = CreateManager();
        try
        {
            var request = new VoiceRequest("見つからない", 1);
            var data = await mgr.GetCachedAudioAsync(request);
            await Assert.That(data).IsNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ProcessTextSegments_PreservesInputOrder()
    {
        // #14 並列 → 逐次化のリグレッション:
        // 入力順がそのまま結果順になることをテストする (Task.WhenAll では順序保証はあるが、
        // 逐次に変えても順序が崩れないことを明示する)
        var (mgr, dir) = CreateManager();
        try
        {
            var request = new VoiceRequest("dummy", 1);
            var segments = new List<TextSegment>
            {
                new() { Text = "first" },
                new() { Text = "second" },
                new() { Text = "third" }
            };

            var result = await mgr.ProcessTextSegmentsAsync(segments, request);

            await Assert.That(result.Count).IsEqualTo(3);
            await Assert.That(result[0].Text).IsEqualTo("first");
            await Assert.That(result[1].Text).IsEqualTo("second");
            await Assert.That(result[2].Text).IsEqualTo("third");
            // どれもキャッシュに無いので IsCached=false が並ぶ
            await Assert.That(result.All(s => !s.IsCached)).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task GetCachedAudioAsync_RespectsCancellation()
    {
        // #11 リグレッション: CancellationToken が伝播することを確認
        var (mgr, dir) = CreateManager();
        try
        {
            var request = new VoiceRequest("キャンセル", 1);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await mgr.GetCachedAudioAsync(request, cts.Token);
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ProcessTextSegmentsAsync_RespectsCancellation()
    {
        var (mgr, dir) = CreateManager();
        try
        {
            var request = new VoiceRequest("dummy", 1);
            var segments = new List<TextSegment>
            {
                new() { Text = "a" },
                new() { Text = "b" }
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await mgr.ProcessTextSegmentsAsync(segments, request, cts.Token);
            });
        }
        finally { Directory.Delete(dir, true); }
    }
}
