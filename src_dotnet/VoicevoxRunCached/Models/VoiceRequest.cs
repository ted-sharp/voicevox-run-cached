using System.Text.Json.Serialization;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Models;

/// <param name="text">読み上げる文</param>
/// <param name="speaker">
/// 話者。エンジンごとの表記 (VOICEVOX はスタイル ID の数字) で、<see cref="Services.Synthesis.SpeechEngineProvider.NormalizeSpeaker"/> で正規化した値
/// (キャッシュキーに入るので、同じ話者は同じ文字列にする)
/// </param>
/// <param name="speed">話速</param>
/// <param name="pitch">音高</param>
/// <param name="volume">音量</param>
/// <param name="engine">合成エンジン</param>
public class VoiceRequest(
    string text = "",
    string speaker = "0",
    double speed = 1.0,
    double pitch = 0.0,
    double volume = 1.0,
    SpeechEngineKind engine = SpeechEngineKind.Voicevox)
{
    public string Text { get; set; } = text;
    public SpeechEngineKind Engine { get; set; } = engine;
    public string Speaker { get; set; } = speaker;
    public double Speed { get; set; } = speed;
    public double Pitch { get; set; } = pitch;
    public double Volume { get; set; } = volume;

    /// <summary>
    /// 音声パラメータ (Engine/Speaker/Speed/Pitch/Volume) を引き継ぎ、Text だけ差し替えた新しい <see cref="VoiceRequest"/> を返す。
    /// セグメント単位の合成 / フィラー / キャッシュ参照で同じ生成パターンを共有するためのヘルパ。
    /// </summary>
    public VoiceRequest WithText(string text) => new()
    {
        Text = text,
        Engine = this.Engine,
        Speaker = this.Speaker,
        Speed = this.Speed,
        Pitch = this.Pitch,
        Volume = this.Volume,
    };
}

// C# 13 Primary constructor for Speaker model
public class Speaker(string name = "", string speakerUuid = "", string version = "")
{
    [JsonPropertyName("name")] public string Name { get; set; } = name;
    [JsonPropertyName("speaker_uuid")] public string SpeakerUuid { get; set; } = speakerUuid;
    [JsonPropertyName("styles")] public List<SpeakerStyle> Styles { get; init; } = [];
    [JsonPropertyName("version")] public string Version { get; set; } = version;
}

// C# 13 Primary constructor for SpeakerStyle model
public class SpeakerStyle(string name = "", int id = 0)
{
    [JsonPropertyName("name")] public string Name { get; set; } = name;
    [JsonPropertyName("id")] public int Id { get; set; } = id;
}
