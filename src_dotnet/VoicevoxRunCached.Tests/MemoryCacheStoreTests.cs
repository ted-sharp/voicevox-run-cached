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
    public async Task TryGet_Miss_ReturnsFalseAndNullData()
    {
        var store = CreateStore();

        var found = store.TryGet("nokey", out var data);

        await Assert.That(found).IsFalse();
        await Assert.That(data).IsNull();
    }

    [Test]
    public async Task SetThenTryGet_ReturnsCachedData()
    {
        var store = CreateStore();
        var bytes = new byte[] { 1, 2, 3, 4 };

        store.Set("k1", bytes);
        var found = store.TryGet("k1", out var data);

        await Assert.That(found).IsTrue();
        await Assert.That(data!.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Clear_RemovesAllEntries()
    {
        var store = CreateStore();
        store.Set("a", new byte[] { 1 });
        store.Set("b", new byte[] { 2 });

        store.Clear();

        await Assert.That(store.TryGet("a", out _)).IsFalse();
        await Assert.That(store.TryGet("b", out _)).IsFalse();
    }
}
