using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VoiceVoxApiClient.BuildAudioQueryUrl のテスト。
/// 浮動小数粒度 (F2) と InvariantCulture フォーマットの回帰防止が主目的。
/// </summary>
public class VoiceVoxApiClientUrlTests
{
    private const string BaseUrl = "http://127.0.0.1:50021";

    [Test]
    public async Task DefaultRequest_OmitsScaleParameters()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.0, 0.0, 1.0), BaseUrl);

        await Assert.That(url).DoesNotContain("speed_scale");
        await Assert.That(url).DoesNotContain("pitch_scale");
        await Assert.That(url).DoesNotContain("volume_scale");
    }

    [Test]
    public async Task NonDefaultSpeed_IncludesSpeedScale()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.5, 0.0, 1.0), BaseUrl);

        await Assert.That(url).Contains("speed_scale=1.5");
    }

    [Test]
    public async Task SpeedNearDefault_RoundedToDefault_OmitsParameter()
    {
        // 1.001 は F2 で 1.00 に丸まり、デフォルトと同じになるので omit される
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.001, 0.0, 1.0), BaseUrl);

        await Assert.That(url).DoesNotContain("speed_scale");
    }

    [Test]
    public async Task SpeedRoundsToTwoDecimals()
    {
        // 1.005 は F2 で 1.01 (banker's rounding により実際は 1.00 になり得るが Math.Round デフォルトは ToEven)
        // 1.014 は F2 で 1.01 (確実に)
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.014, 0.0, 1.0), BaseUrl);

        await Assert.That(url).Contains("speed_scale=1.01");
    }

    [Test]
    public async Task UsesInvariantCulture_EvenUnderGermanLocale()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.5, 0.0, 1.0), BaseUrl);

            // de-DE では 1,5 になるはずだが InvariantCulture で 1.5 になる
            await Assert.That(url).Contains("speed_scale=1.5");
            await Assert.That(url).DoesNotContain("speed_scale=1,5");
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public async Task PitchAndVolume_BothIncludedWhenNonDefault()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.0, 0.25, 0.8), BaseUrl);

        await Assert.That(url).Contains("pitch_scale=0.25");
        await Assert.That(url).Contains("volume_scale=0.8");
        await Assert.That(url).DoesNotContain("speed_scale");
    }

    [Test]
    public async Task SpeakerIdAndText_AlwaysIncluded()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("hello", 42), BaseUrl);

        await Assert.That(url).Contains("speaker=42");
        await Assert.That(url).Contains("text=hello");
    }

    [Test]
    public async Task JapaneseText_IsPercentEncoded()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("こんにちは", 1), BaseUrl);

        // 「こ」は UTF-8 で 0xE3 0x81 0x93 → %E3%81%93
        await Assert.That(url).Contains("%E3%81%93");
        await Assert.That(url).DoesNotContain("こんにちは");
    }

    [Test]
    public async Task SpecialCharsInText_AreEscaped()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("a b&c=d", 1), BaseUrl);

        await Assert.That(url).Contains("text=a%20b%26c%3Dd");
    }

    [Test]
    public async Task BaseUrl_IsPrefixedAsIs()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("x", 1), "http://example.com:9999");

        await Assert.That(url).StartsWith("http://example.com:9999/audio_query?");
    }

    [Test]
    public async Task PitchNegative_IsIncludedWithSign()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.0, -0.15, 1.0), BaseUrl);

        await Assert.That(url).Contains("pitch_scale=-0.15");
    }

    [Test]
    public async Task RoundedValuesMatchCacheKeyGenerator()
    {
        // CacheKeyGenerator も F2 で正規化するため、Speed=1.001 と Speed=1.004 は同じキーを生む。
        // それと整合するように、API 側でも両者は同じ omit 判定 (デフォルトと同じ) になるべき。
        var u1 = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.001, 0.0, 1.0), BaseUrl);
        var u2 = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.004, 0.0, 1.0), BaseUrl);

        await Assert.That(u1).IsEqualTo(u2);
    }
}
