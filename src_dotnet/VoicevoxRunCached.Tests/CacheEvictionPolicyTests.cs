using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class CacheEvictionPolicyTests
{
    private static (CacheEvictionPolicy policy, DiskCacheStore disk, string dir) Create(int expirationDays = 30, double maxSizeGb = 1.0, IClock? clock = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_evict_" + Guid.NewGuid().ToString("N"));
        var settings = new CacheSettings
        {
            Directory = dir,
            ExpirationDays = expirationDays,
            MaxSizeGb = maxSizeGb,
            UseExecutableBaseDirectory = false
        };
        clock ??= SystemClock.Instance;
        var disk = new DiskCacheStore(settings, NullLogger.Instance, clock);
        return (new CacheEvictionPolicy(disk, settings, NullLogger.Instance, clock), disk, dir);
    }

    private static async Task SeedAsync(DiskCacheStore disk, string key, byte[] data, DateTime createdAt, string text = "seed")
    {
        var request = new VoiceRequest(text, 1);
        await disk.SaveAsync(request, data, key);

        // メタの CreatedAt を上書き
        var metaPath = Path.Combine(disk.Directory, $"{key}.meta.json");
        var meta = JsonSerializer.Deserialize<CacheMetadata>(await File.ReadAllTextAsync(metaPath));
        meta!.CreatedAt = createdAt;
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta));

        // eviction は最終利用日時 (meta.json の更新日時) で古さを判定する。保存直後は CreatedAt と同じ
        File.SetLastWriteTimeUtc(metaPath, createdAt);
    }

    [Test]
    public async Task CleanupExpiredAsync_KeepsEntriesWithinExpiration()
    {
        var (policy, disk, dir) = Create(expirationDays: 7);
        try
        {
            await SeedAsync(disk, "recent", new byte[10], DateTime.UtcNow.AddDays(-6));

            await policy.CleanupExpiredAsync();

            await Assert.That(File.Exists(Path.Combine(dir, "recent.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "recent.meta.json"))).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task CleanupExpiredAsync_RemovesOnlyExpired()
    {
        var (policy, disk, dir) = Create(expirationDays: 7);
        try
        {
            await SeedAsync(disk, "fresh", new byte[10], DateTime.UtcNow.AddDays(-1));
            await SeedAsync(disk, "stale", new byte[10], DateTime.UtcNow.AddDays(-30));

            await policy.CleanupExpiredAsync();

            await Assert.That(File.Exists(Path.Combine(dir, "fresh.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "stale.opus"))).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task CleanupByMaxSize_KeepsUnderLimitWhenAlreadyOk()
    {
        var (policy, disk, dir) = Create(maxSizeGb: 1.0);
        try
        {
            await SeedAsync(disk, "k1", new byte[100], DateTime.UtcNow);
            await SeedAsync(disk, "k2", new byte[100], DateTime.UtcNow);

            // 上限 1GB なので 200B では削除されない
            await policy.CleanupByMaxSizeAsync();

            await Assert.That(File.Exists(Path.Combine(dir, "k1.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "k2.opus"))).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task CleanupByMaxSize_DeletesOldestFirstWhenExceeded()
    {
        // 上限 ~ 100bytes (1e-7 GB ≈ 107B)
        var (policy, disk, dir) = Create(maxSizeGb: 1e-7);
        try
        {
            // 古い順に "old" → "new"
            await SeedAsync(disk, "old", new byte[200], DateTime.UtcNow.AddDays(-2));
            await SeedAsync(disk, "new", new byte[200], DateTime.UtcNow);

            await policy.CleanupByMaxSizeAsync();

            // 古いものから削除される
            await Assert.That(File.Exists(Path.Combine(dir, "old.opus"))).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task CleanupByMaxSize_KeepsRecentlyUsedEntry()
    {
        // 回帰: 作成日時の古い順に消していたため、よく使う定型文ほど先に消えていた。
        // 上限 1500B に 1000B × 2 件 → 1 件消える。作成は "used" が古いが、直前に読んだので "unused" が消える
        var clock = new MutableClock(DateTime.UtcNow);
        var (policy, disk, dir) = Create(maxSizeGb: 1500.0 / (1024 * 1024 * 1024), clock: clock);
        try
        {
            var start = clock.UtcNow;
            await disk.SaveAsync(new VoiceRequest("used", 1), new byte[1000], "used");
            clock.UtcNow = start.AddDays(1);
            await disk.SaveAsync(new VoiceRequest("unused", 1), new byte[1000], "unused");

            clock.UtcNow = start.AddDays(2);
            await Assert.That(await disk.LoadAsync("used")).IsNotNull();

            await policy.CleanupByMaxSizeAsync();

            await Assert.That(File.Exists(Path.Combine(dir, "used.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "unused.opus"))).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
