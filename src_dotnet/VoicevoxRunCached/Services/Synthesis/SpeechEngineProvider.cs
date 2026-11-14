using System.Globalization;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// <see cref="SpeechEngineKind"/> から <see cref="ISpeechEngine"/> を取り出す。エンジンごとの話者の既定値と正規化もここに置く。
/// </summary>
/// <remarks>
/// エンジンを足すときは、コンストラクタ・<see cref="All"/>・<see cref="Get"/>・<see cref="DefaultSpeaker"/>・<see cref="NormalizeSpeaker"/> に追加する。
/// </remarks>
public class SpeechEngineProvider
{
    private readonly VoicevoxEngine _voicevox;

    public SpeechEngineProvider(VoicevoxEngine voicevox)
    {
        _voicevox = voicevox ?? throw new ArgumentNullException(nameof(voicevox));
        All = [_voicevox];
    }

    /// <summary>
    /// すべてのエンジン (<see cref="SpeechLock"/> が、起動したエンジンを止めるために使う)。
    /// </summary>
    public IReadOnlyList<ISpeechEngine> All { get; }

    public ISpeechEngine Get(SpeechEngineKind kind) => kind switch
    {
        SpeechEngineKind.Voicevox => _voicevox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
    };

    /// <summary>
    /// <c>--speaker</c> を省略したときの話者 (各エンジンのセクションの <c>DefaultSpeaker</c>)。
    /// </summary>
    public static string DefaultSpeaker(SpeechEngineKind kind, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return kind switch
        {
            SpeechEngineKind.Voicevox => settings.VoiceVox.DefaultSpeaker.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
        };
    }

    /// <summary>
    /// <c>--speaker</c> の値をエンジンごとに検証し、正規化する (同じ話者が同じキャッシュキーになるように)。
    /// </summary>
    /// <returns>正規化した話者。不正なら null で、理由を <paramref name="error"/> に返す</returns>
    public static string? NormalizeSpeaker(SpeechEngineKind kind, string raw, out string? error) => kind switch
    {
        SpeechEngineKind.Voicevox => VoicevoxEngine.NormalizeSpeaker(raw, out error),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
    };
}
