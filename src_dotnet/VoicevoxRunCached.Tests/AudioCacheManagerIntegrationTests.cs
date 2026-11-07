using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class AudioCacheManagerIntegrationTests
{
    private static (AudioCacheManager manager, string dir) CreateManager(double maxSizeGb = 1.0, IClock? clock = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_acm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new CacheSettings
        {
            Directory = dir,
            ExpirationDays = 30,
            MaxSizeGb = maxSizeGb,
            UseExecutableBaseDirectory = false
        };
        return (new AudioCacheManager(settings, NullLogger.Instance, clock ?? SystemClock.Instance), dir);
    }

    [Test]
    public async Task GetCachedAudio_UsesInjectedClockForExpiration()
    {
        // AudioCacheManager が IClock を受け取らず、期限判定が常に実時刻だった回帰の検知
        var clock = new MutableClock(DateTime.UtcNow);
        var (mgr, dir) = CreateManager(clock: clock);
        try
        {
            var request = new VoiceRequest("期限", 1);
            await mgr.SaveAudioCacheAsync(request, AudioConversionUtility.CreateMinimalWavData(300, 24000));

            clock.UtcNow = clock.UtcNow.AddDays(31); // ExpirationDays = 30

            var cached = await mgr.GetCachedAudioAsync(request);

            await Assert.That(cached).IsNull();
        }
        finally { Directory.Delete(dir, true); }
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

            await Assert.That(Directory.GetFiles(dir, "*.opus").Length).IsEqualTo(2);

            await mgr.RunMaintenanceAsync();

            await Assert.That(Directory.GetFiles(dir, "*.opus").Length).IsLessThan(2);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task SaveThenGet_RoundTripsAsPlayableOpus()
    {
        var (mgr, dir) = CreateManager();
        try
        {
            var request = new VoiceRequest("往復", 1, 1.2, 0.0, 1.0);
            await mgr.SaveAudioCacheAsync(request, AudioConversionUtility.CreateMinimalWavData(300, 24000));

            var cached = await mgr.GetCachedAudioAsync(request);

            await Assert.That(cached).IsNotNull();
            await Assert.That(AudioFormatDetector.DetectFormat(cached!)).IsEqualTo(AudioFormat.Opus);
            using var reader = new AudioFormatDetector(NullLogger.Instance).CreateWaveStream(cached!);
            await Assert.That(reader.TotalTime.TotalMilliseconds).IsGreaterThan(250);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ClearAllCache_RemovesOpusAndLegacyMp3()
    {
        var (mgr, dir) = CreateManager();
        try
        {
            await mgr.SaveAudioCacheAsync(new VoiceRequest("新", 1), AudioConversionUtility.CreateMinimalWavData(300, 24000));
            await File.WriteAllBytesAsync(Path.Combine(dir, "legacy.mp3"), [0xFF, 0xFB]);
            await File.WriteAllTextAsync(Path.Combine(dir, "legacy.meta.json"), "{}");
            // 書き込みの途中で落ちて残った一時ファイルも消す
            await File.WriteAllBytesAsync(Path.Combine(dir, "interrupted.opus.tmp"), [0x00]);

            await mgr.ClearAllCacheAsync();

            await Assert.That(Directory.GetFiles(dir)).IsEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ClearAllCache_LockedFile_ThrowsCacheException()
    {
        // 削除できないファイルがあるのに --clear が成功表示になっていた回帰の検知
        var (mgr, dir) = CreateManager();
        try
        {
            await mgr.SaveAudioCacheAsync(new VoiceRequest("ロック", 1), AudioConversionUtility.CreateMinimalWavData(300, 24000));
            var lockedPath = Directory.GetFiles(dir, "*.opus").Single();

            // FileShare.None で開いたままにすると Windows では File.Delete が共有違反 (IOException) になる
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<CacheException>(() => mgr.ClearAllCacheAsync());
            }
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
