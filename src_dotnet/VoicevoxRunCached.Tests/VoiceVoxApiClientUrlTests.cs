using System.Text.Json.Nodes;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VoiceVoxApiClient.BuildAudioQueryUrl / ApplyVoiceParameters と、エンジンに送るクエリのテスト。
/// VOICEVOX の /audio_query はクエリの speed_scale 等を無視するため、話速・音高・音量は JSON 側に設定する。
/// </summary>
public class VoiceVoxApiClientUrlTests
{
    private const string BaseUrl = "http://127.0.0.1:50021";

    // /audio_query が返す JSON の最小形 (エンジンは常に既定値を返す)
    private const string EngineQuery =
        """{"accent_phrases":[],"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0,"volumeScale":1.0,"outputSamplingRate":24000,"kana":"ア"}""";

    private static JsonObject Apply(VoiceRequest request)
        => JsonNode.Parse(VoiceVoxApiClient.ApplyVoiceParameters(EngineQuery, request))!.AsObject();

    [Test]
    public async Task Url_ContainsOnlyTextAndSpeaker()
    {
        var url = VoiceVoxApiClient.BuildAudioQueryUrl(new VoiceRequest("text", 1, 1.5, 0.1, 0.5), BaseUrl);

        await Assert.That(url).IsEqualTo($"{BaseUrl}/audio_query?text=text&speaker=1");
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
    public async Task ApplyVoiceParameters_OverwritesScales()
    {
        var query = Apply(new VoiceRequest("text", 1, 1.5, -0.15, 0.8));

        await Assert.That(query["speedScale"]!.GetValue<double>()).IsEqualTo(1.5);
        await Assert.That(query["pitchScale"]!.GetValue<double>()).IsEqualTo(-0.15);
        await Assert.That(query["volumeScale"]!.GetValue<double>()).IsEqualTo(0.8);
    }

    [Test]
    public async Task ApplyVoiceParameters_KeepsOtherFields()
    {
        var query = Apply(new VoiceRequest("text", 1, 1.5, 0.0, 1.0));

        await Assert.That(query["intonationScale"]!.GetValue<double>()).IsEqualTo(1.0);
        await Assert.That(query["outputSamplingRate"]!.GetValue<int>()).IsEqualTo(24000);
        await Assert.That(query["kana"]!.GetValue<string>()).IsEqualTo("ア");
        await Assert.That(query["accent_phrases"]).IsNotNull();
    }

    [Test]
    public async Task ApplyVoiceParameters_RoundsSameAsCacheKey()
    {
        // 0.065 は Math.Round(0.065, 2) = 0.06 だが ToString("F2") = "0.07" になる (丸め方式の差)。
        // CacheKeyGenerator は F2 なので 0.065 と 0.07 は同じキー → 合成パラメータも同じでなければならない。
        var midpoint = new VoiceRequest("text", 1, 1.0, 0.065, 1.0);
        var rounded = new VoiceRequest("text", 1, 1.0, 0.07, 1.0);

        await Assert.That(CacheKeyGenerator.Compute(midpoint)).IsEqualTo(CacheKeyGenerator.Compute(rounded));
        await Assert.That(VoiceVoxApiClient.ApplyVoiceParameters(EngineQuery, midpoint))
            .IsEqualTo(VoiceVoxApiClient.ApplyVoiceParameters(EngineQuery, rounded));
        await Assert.That(Apply(midpoint)["pitchScale"]!.GetValue<double>()).IsEqualTo(0.07);
    }

    [Test]
    public async Task ApplyVoiceParameters_IsCultureInvariant()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var json = VoiceVoxApiClient.ApplyVoiceParameters(EngineQuery, new VoiceRequest("text", 1, 1.5, 0.0, 1.0));

            await Assert.That(json).Contains("\"speedScale\":1.5");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public async Task InitializeSpeaker_SkipsReinitialization()
    {
        // 回帰: skip_reinit を付けず、読み込み済みの話者でも毎回初期化し直していた (実測で 1 回 1.7 秒ほど)
        using var engine = new FakeEngineServer();
        using var apiClient = engine.CreateApiClient();

        await apiClient.InitializeSpeakerAsync(FakeEngineServer.SpeakerId);

        await Assert.That(engine.LastInitializeSpeakerQuery).Contains("skip_reinit=true");
    }
}
