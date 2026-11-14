using System.Globalization;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// <see cref="SpeechEngineKind"/> から <see cref="ISpeechEngine"/> を取り出す。エンジンごとの話者の既定値と正規化もここに置く。
/// </summary>
/// <remarks>
/// エンジンを足すときは、コンストラクタ・<see cref="All"/>・<see cref="Get"/>・<see cref="DefaultSpeaker"/>・<see cref="NormalizeSpeaker"/>・
/// <see cref="ValidateParameters"/> に追加する。
/// </remarks>
public class SpeechEngineProvider
{
    private readonly VoicevoxEngine _voicevox;
    private readonly VoicepeakEngine _voicepeak;

    public SpeechEngineProvider(VoicevoxEngine voicevox, VoicepeakEngine voicepeak)
    {
        _voicevox = voicevox ?? throw new ArgumentNullException(nameof(voicevox));
        _voicepeak = voicepeak ?? throw new ArgumentNullException(nameof(voicepeak));
        All = [_voicevox, _voicepeak];
    }

    /// <summary>
    /// すべてのエンジン (<see cref="SpeechLock"/> が、起動したエンジンを止めるために使う)。
    /// </summary>
    public IReadOnlyList<ISpeechEngine> All { get; }

    public ISpeechEngine Get(SpeechEngineKind kind) => kind switch
    {
        SpeechEngineKind.Voicevox => _voicevox,
        SpeechEngineKind.Voicepeak => _voicepeak,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
    };

    /// <summary>
    /// <c>--speaker</c> を省略したときの話者 (各エンジンのセクションの <c>DefaultSpeaker</c>)。未設定なら空文字列。
    /// </summary>
    public static string DefaultSpeaker(SpeechEngineKind kind, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return kind switch
        {
            SpeechEngineKind.Voicevox => settings.VoiceVox.DefaultSpeaker.ToString(CultureInfo.InvariantCulture),
            SpeechEngineKind.Voicepeak => settings.Voicepeak.DefaultSpeaker,
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
        SpeechEngineKind.Voicepeak => VoicepeakEngine.NormalizeSpeaker(raw, out error),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
    };

    /// <summary>
    /// 話速・音高・音量がエンジンで指定できる範囲か (共通の検証 (<c>--speed</c> は 0 より大きい等) の後に、エンジンごとの範囲を見る)。
    /// </summary>
    /// <returns>範囲外ならオプション名と理由。範囲内なら null</returns>
    public static (string Option, string Reason)? ValidateParameters(VoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Engine switch
        {
            SpeechEngineKind.Voicevox => null,
            SpeechEngineKind.Voicepeak => VoicepeakEngine.ValidateParameters(request),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Engine, "未対応のエンジンです"),
        };
    }
}
