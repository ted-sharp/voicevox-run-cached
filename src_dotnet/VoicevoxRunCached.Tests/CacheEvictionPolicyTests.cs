using System.Text.Json;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

public class CacheEvictionPolicyTests
{
    private static (CacheEvictionPolicy policy, DiskCacheStore disk, string dir) Create(int expirationDays = 30, double maxSizeGb = 1.0)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_evict_" + Guid.NewGuid().ToString("N"));
        var settings = new CacheSettings
        {
            Directory = dir,
            ExpirationDays = expirationDays,
            MaxSizeGb = maxSizeGb,
            MemoryCacheSizeMb = 100,
            UseExecutableBaseDirectory = false
        };
        var disk = new DiskCacheStore(settings);
        return (new CacheEvictionPolicy(disk, settings), disk, dir);
    }

    private static async Task SeedAsync(DiskCacheStore disk, string key, byte[] data, DateTime createdAt, string text = "seed")
    {
        var request = new VoiceRequest(text, 1);
        await disk.SaveAsync(request, data, key);

        // メタの CreatedAt を上書き
        var meta = await disk.ReadMetadataAsync(key);
        meta!.CreatedAt = createdAt;
        var metaPath = Path.Combine(disk.Directory, $"{key}.meta.json");
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(meta));
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

            await Assert.That(File.Exists(Path.Combine(dir, "fresh.mp3"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "stale.mp3"))).IsFalse();
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
            policy.CleanupByMaxSize();

            await Assert.That(File.Exists(Path.Combine(dir, "k1.mp3"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "k2.mp3"))).IsTrue();
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

            policy.CleanupByMaxSize();

            // 古いものから削除される
            await Assert.That(File.Exists(Path.Combine(dir, "old.mp3"))).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }
}
