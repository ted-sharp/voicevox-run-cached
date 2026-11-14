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
    private readonly CevioEngine _cevio;

    public SpeechEngineProvider(VoicevoxEngine voicevox, CevioEngine cevio)
    {
        _voicevox = voicevox ?? throw new ArgumentNullException(nameof(voicevox));
        _cevio = cevio ?? throw new ArgumentNullException(nameof(cevio));
        All = [_voicevox, _cevio];
    }

    /// <summary>
    /// すべてのエンジン (<see cref="SpeechLock"/> が、起動したエンジンを止めるために使う)。
    /// </summary>
    public IReadOnlyList<ISpeechEngine> All { get; }

    public ISpeechEngine Get(SpeechEngineKind kind) => kind switch
    {
        SpeechEngineKind.Voicevox => _voicevox,
        SpeechEngineKind.Cevio => _cevio,
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
            SpeechEngineKind.Cevio => CevioDefaultSpeaker(settings.Cevio),
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
        SpeechEngineKind.Cevio => CevioEngine.NormalizeSpeaker(raw, out error),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未対応のエンジンです"),
    };

    /// <remarks>
    /// CeVIO はキャストに決まった既定が無いので、<c>Cevio.DefaultSpeaker</c> が空なら <c>-s</c> を求める。
    /// 書式は設定の検証 (<see cref="CevioSettingsValidator"/>) で確かめてある。
    /// </remarks>
    private static string CevioDefaultSpeaker(CevioSettings settings)
    {
        if (String.IsNullOrWhiteSpace(settings.DefaultSpeaker))
        {
            throw new Exceptions.VoicevoxRunCachedException(
                errorCode: Exceptions.ErrorCodes.General.InvalidArguments,
                message: "Cevio.DefaultSpeaker is not set",
                userMessage: "CeVIO のキャストが指定されていません。",
                suggestedSolution: "-s でキャスト名を指定するか、appsettings.json の Cevio.DefaultSpeaker を設定してください (`VoicevoxRunCached speakers --engine cevio` で確認できます)。");
        }
        return CevioEngine.NormalizeSpeaker(settings.DefaultSpeaker, out var error)
            ?? throw new InvalidOperationException($"Cevio.DefaultSpeaker が不正です: {error}");
    }
}
