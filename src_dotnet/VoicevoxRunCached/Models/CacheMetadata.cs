using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Models;

/// <summary>
/// キャッシュエントリのメタデータ (.meta.json に保存)
/// </summary>
public class CacheMetadata
{
    public DateTime CreatedAt { get; set; }
    public string Text { get; set; } = String.Empty;

    /// <summary>
    /// 合成したエンジン。エンジン対応前の meta.json には無い (VOICEVOX)。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<SpeechEngineKind>))]
    public SpeechEngineKind Engine { get; set; } = SpeechEngineKind.Voicevox;

    /// <summary>
    /// 話者 (<see cref="VoiceRequest.Speaker"/>)。
    /// </summary>
    /// <remarks>
    /// JSON 名はエンジン対応前と同じ <c>SpeakerId</c>。当時は数値で書いていたので、数値も文字列として読む
    /// (読めないと <see cref="Services.Cache.DiskCacheStore.IsMetadataValid"/> を通らず、既存のキャッシュが全部消える)。
    /// </remarks>
    [JsonPropertyName("SpeakerId")]
    [JsonConverter(typeof(NumberOrStringConverter))]
    public string Speaker { get; set; } = String.Empty;

    public double Speed { get; set; }
    public double Pitch { get; set; }
    public double Volume { get; set; }

    /// <summary>
    /// 数値も文字列として読む (書くときは文字列)。
    /// </summary>
    private sealed class NumberOrStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Number
                ? reader.GetInt64().ToString(CultureInfo.InvariantCulture)
                : reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
