using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Infrastructure;

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
            UseExecutableBaseDirectory = false
        };
        return (new DiskCacheStore(settings, NullLogger.Instance, SystemClock.Instance), dir);
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

            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.opus"))).IsTrue();
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
            var meta = System.Text.Json.JsonSerializer.Deserialize<CacheMetadata>(await File.ReadAllTextAsync(metaPath));
            await Assert.That(meta).IsNotNull();
            meta!.CreatedAt = DateTime.UtcNow.AddDays(-2);
            await File.WriteAllTextAsync(metaPath,
                System.Text.Json.JsonSerializer.Serialize(meta));

            var loaded = await store.LoadAsync(key);

            await Assert.That(loaded).IsNull();
            // 期限切れエントリは削除される
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.opus"))).IsFalse();
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

            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.opus"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.meta.json"))).IsFalse();
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
            CreatedAt = DateTime.UtcNow,
        })).IsTrue();

        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = 1,
            CreatedAt = default,
        })).IsFalse();

        await Assert.That(DiskCacheStore.IsMetadataValid(new CacheMetadata
        {
            Text = "ok",
            SpeakerId = 1,
            CreatedAt = DateTime.UtcNow,
        })).IsTrue();
    }

    [Test]
    public async Task LoadAsync_LegacyMp3Only_Misses()
    {
        var (store, dir) = CreateStore();
        try
        {
            const string key = "legacy";
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{key}.mp3"), [0xFF, 0xFB]);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{key}.meta.json"),
                $$"""{"CreatedAt":"{{DateTime.UtcNow:O}}","Text":"旧","SpeakerId":1,"Speed":1.0,"Pitch":0.0,"Volume":1.0}""");

            await Assert.That(await store.LoadAsync(key)).IsNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task DeleteLegacyFiles_RemovesMp3_AndOrphanMetaOnly()
    {
        var (store, dir) = CreateStore();
        try
        {
            // orphan: MP3 しか無い → meta も消す
            await File.WriteAllBytesAsync(Path.Combine(dir, "orphan.mp3"), [0x01]);
            await File.WriteAllTextAsync(Path.Combine(dir, "orphan.meta.json"), "{}");
            // migrated: 同じキーで Opus 再合成済み → meta は Opus 側のものなので残す
            await File.WriteAllBytesAsync(Path.Combine(dir, "migrated.mp3"), [0x01]);
            await File.WriteAllBytesAsync(Path.Combine(dir, "migrated.opus"), [0x01]);
            await File.WriteAllTextAsync(Path.Combine(dir, "migrated.meta.json"), "{}");

            var deleted = store.DeleteLegacyFiles();

            await Assert.That(deleted).IsEqualTo(2);
            await Assert.That(Directory.GetFiles(dir, "*.mp3")).IsEmpty();
            await Assert.That(File.Exists(Path.Combine(dir, "orphan.meta.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir, "migrated.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "migrated.meta.json"))).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task LoadAsync_Cancelled_ThrowsAndKeepsFiles()
    {
        // Ctrl+C で読み込みが中断されたキャッシュを破損扱いで削除していた回帰の検知
        var (store, dir) = CreateStore();
        try
        {
            var key = "key-cancel";
            await store.SaveAsync(new VoiceRequest("キャンセル", 1), [0x01, 0x02], key);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(() => store.LoadAsync(key, cts.Token));

            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.opus"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, $"{key}.meta.json"))).IsTrue();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public async Task ExtractKeyFromMetadataPath_StripsExtensions()
    {
        var key = DiskCacheStore.ExtractKeyFromMetadataPath("/some/path/abc123.meta.json");
        await Assert.That(key).IsEqualTo("abc123");
    }
}
