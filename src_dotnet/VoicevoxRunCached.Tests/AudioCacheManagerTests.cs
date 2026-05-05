using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Tests;

public class AudioCacheManagerTests
{
    [Test]
    public async Task ComputeCacheKey_ReturnsLowercaseHex64Chars()
    {
        var request = new VoiceRequest("こんにちは", 1, 1.0, 0.0, 1.0);

        var key = CacheKeyGenerator.Compute(request);

        await Assert.That(key.Length).IsEqualTo(64);
        await Assert.That(key).Matches("^[0-9a-f]+$");
    }

    [Test]
    public async Task ComputeCacheKey_IsStableForSameInput()
    {
        var request1 = new VoiceRequest("テスト", 2, 1.5, 0.1, 0.8);
        var request2 = new VoiceRequest("テスト", 2, 1.5, 0.1, 0.8);

        var key1 = CacheKeyGenerator.Compute(request1);
        var key2 = CacheKeyGenerator.Compute(request2);

        await Assert.That(key1).IsEqualTo(key2);
    }

    [Test]
    public async Task ComputeCacheKey_DiffersWhenTextChanges()
    {
        var a = CacheKeyGenerator.Compute(new VoiceRequest("foo", 1));
        var b = CacheKeyGenerator.Compute(new VoiceRequest("bar", 1));

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task ComputeCacheKey_DiffersWhenSpeakerChanges()
    {
        var a = CacheKeyGenerator.Compute(new VoiceRequest("text", 1));
        var b = CacheKeyGenerator.Compute(new VoiceRequest("text", 2));

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task ComputeCacheKey_DiffersWhenSpeedChanges()
    {
        var a = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, speed: 1.0));
        var b = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, speed: 1.5));

        await Assert.That(a).IsNotEqualTo(b);
    }

    [Test]
    public async Task ComputeCacheKey_IsCultureInvariantForFloats()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var keyDe = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, 1.5, 0.25, 0.8));

            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var keyInv = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, 1.5, 0.25, 0.8));

            await Assert.That(keyDe).IsEqualTo(keyInv);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public async Task ComputeCacheKey_TreatsCloseFloatsAsSameDueTo2DecimalRounding()
    {
        var a = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, 1.001, 0.0, 1.0));
        var b = CacheKeyGenerator.Compute(new VoiceRequest("text", 1, 1.004, 0.0, 1.0));

        await Assert.That(a).IsEqualTo(b);
    }
}
