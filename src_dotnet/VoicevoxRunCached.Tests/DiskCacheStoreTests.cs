using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

public class DiskCacheStoreTests
{
    private static (DiskCacheStore store, string dir) CreateStore(int expirationDays = 30)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_disk_" + Guid.NewGuid().ToString("N"));
        var settings = new CacheSettings
        {
            Directory = dir,
            ExpirationDays = expirationDays,
            MaxSizeGb = 1.0,
            MemoryCacheSizeMb = 100,
            UseExecutableBaseDirectory = false
        };
        return (new DiskCacheStore(settings), dir);
    }

    [Test]
    public async Task SaveAsync_CreatesAudioAndMetaFiles()
    {
        var (store, dir) = CreateStore();
        try
        {
            var request = new VoiceRequest("テスト", 1, 1.0, 0.0, 1.0);
            var key = "abc123";
            var data = new byte[] { 0x01, 0x02, 0x03 };

            await store.SaveAsync(request, data, key);

            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.mp3"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.meta.json"))).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task LoadAsync_ReturnsSavedData()
    {
        var (store, dir) = CreateStore();
        try
        {
            var request = new VoiceRequest("テスト読み戻し", 1);
            var key = "key-roundtrip";
            var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };

            await store.SaveAsync(request, data, key);
            var loaded = await store.LoadAsync(key);

            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded!.Length).IsEqualTo(4);
            await Assert.That(loaded[0]).IsEqualTo((byte)0xAA);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task LoadAsync_MissingFiles_ReturnsNull()
    {
        var (store, dir) = CreateStore();
        try
        {
            var loaded = await store.LoadAsync("nonexistent-key");
            await Assert.That(loaded).IsNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task LoadAsync_ExpiredMetadata_DeletesAndReturnsNull()
    {
        var (store, dir) = CreateStore(expirationDays: 1);
        try
        {
            var request = new VoiceRequest("古いキャッシュ", 1);
            var key = "expired-key";
            var data = new byte[] { 0x01 };

            await store.SaveAsync(request, data, key);

            // メタデータの CreatedAt を 2 日前に書き換える
            var metaPath = Path.Combine(dir, $"{key}.meta.json");
            var meta = await store.ReadMetadataAsync(key);
            await Assert.That(meta).IsNotNull();
            meta!.CreatedAt = DateTime.UtcNow.AddDays(-2);
            await File.WriteAllTextAsync(metaPath,
                System.Text.Json.JsonSerializer.Serialize(meta));

            var loaded = await store.LoadAsync(key);

            await Assert.That(loaded).IsNull();
            // 期限切れエントリは削除される
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.mp3"))).IsFalse();
            await Assert.That(File.Exists(metaPath)).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task Delete_RemovesBothFiles()
    {
        var (store, dir) = CreateStore();
        try
        {
            var request = new VoiceRequest("削除", 1);
            var key = "to-delete";
            await store.SaveAsync(request, new byte[] { 0x01 }, key);

            store.Delete(key);

            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.mp3"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.meta.json"))).IsFalse();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task GetStatistics_ReportsFileCountAndSize()
    {
        var (store, dir) = CreateStore();
        try
        {
            var request = new VoiceRequest("stats", 1);
            await store.SaveAsync(request, new byte[100], "k1");
            await store.SaveAsync(request, new byte[200], "k2");

            var (count, size) = store.GetStatistics();

            await Assert.That(count).IsEqualTo(2);
            await Assert.That(size).IsEqualTo(300L);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task IsMetadataValid_RejectsEmptyOrInvalid()
    {
        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "",
            SpeakerId = 1,
            CreatedAt = DateTime.UtcNow
        })).IsFalse();

        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = -1,
            CreatedAt = DateTime.UtcNow
        })).IsFalse();

        // speaker 0 は VOICEVOX に実在する ID なので有効
        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = 0,
            CreatedAt = DateTime.UtcNow
        })).IsTrue();

        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = 1,
            CreatedAt = default
        })).IsFalse();

        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = 1,
            CreatedAt = DateTime.UtcNow
        })).IsTrue();
    }

    [Test]
    public async Task ExtractKeyFromMetadataPath_StripsExtensions()
    {
        var key = DiskCacheStore.ExtractKeyFromMetadataPath("/some/path/abc123.meta.json");
        await Assert.That(key).IsEqualTo("abc123");
    }
}
