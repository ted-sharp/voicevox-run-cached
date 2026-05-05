using Microsoft.Extensions.Caching.Memory;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

public class MemoryCacheStoreTests
{
    private static MemoryCacheStore CreateStore()
    {
        var memCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 * 1024 });
        return new MemoryCacheStore(memCache, TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task TryGet_Miss_IncrementsMissCount()
    {
        var store = CreateStore();

        var found = store.TryGet("nokey", out var data);

        await Assert.That(found).IsFalse();
        await Assert.That(data).IsNull();
        await Assert.That(store.MissCount).IsEqualTo(1L);
        await Assert.That(store.HitCount).IsEqualTo(0L);
    }

    [Test]
    public async Task SetThenTryGet_HitCountIncreases()
    {
        var store = CreateStore();
        var bytes = new byte[] { 1, 2, 3, 4 };

        store.Set("k1", bytes);
        var found = store.TryGet("k1", out var data);

        await Assert.That(found).IsTrue();
        await Assert.That(data!.Length).IsEqualTo(4);
        await Assert.That(store.HitCount).IsEqualTo(1L);
        await Assert.That(store.EntryCount).IsEqualTo(1);
    }

    [Test]
    public async Task RecordHit_DoesNotChangeMissCount()
    {
        var store = CreateStore();

        store.RecordHit();
        store.RecordHit();

        await Assert.That(store.HitCount).IsEqualTo(2L);
        await Assert.That(store.MissCount).IsEqualTo(0L);
    }

    [Test]
    public async Task Clear_RemovesAllEntries()
    {
        var store = CreateStore();
        store.Set("a", new byte[] { 1 });
        store.Set("b", new byte[] { 2 });

        store.Clear();

        await Assert.That(store.EntryCount).IsEqualTo(0);
        // クリア後の TryGet は miss
        await Assert.That(store.TryGet("a", out _)).IsFalse();
    }
}
