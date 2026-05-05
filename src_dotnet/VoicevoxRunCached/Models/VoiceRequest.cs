using System.Text.Json.Serialization;

namespace VoicevoxRunCached.Models;

// C# 13 Primary constructor for cleaner initialization
public class VoiceRequest(string text = "", int speakerId = 0, double speed = 1.0, double pitch = 0.0, double volume = 1.0)
{
    public string Text { get; set; } = text;
    public int SpeakerId { get; set; } = speakerId;
    public double Speed { get; set; } = speed;
    public double Pitch { get; set; } = pitch;
    public double Volume { get; set; } = volume;

    /// <summary>
    /// 音声パラメータ (SpeakerId/Speed/Pitch/Volume) を引き継ぎ、Text だけ差し替えた新しい <see cref="VoiceRequest"/> を返す。
    /// セグメント単位の合成 / フィラー / キャッシュ参照で同じ生成パターンを共有するためのヘルパ。
    /// </summary>
    public VoiceRequest WithText(string text) => new()
    {
        Text = text,
        SpeakerId = this.SpeakerId,
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
