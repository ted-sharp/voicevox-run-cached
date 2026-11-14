using System.Globalization;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// VOICEVOX (と AIVIS Speech 等の互換エンジン) の REST API による <see cref="ISpeechEngine"/>。
/// </summary>
/// <remarks>
/// 話者はスタイル ID (0 以上の整数) を文字列にしたもの。
/// </remarks>
public class VoicevoxEngine : ISpeechEngine
{
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly VoiceVoxApiClient _apiClient;

    public VoicevoxEngine(VoiceVoxEngineManager engineManager, VoiceVoxApiClient apiClient)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public SpeechEngineKind Kind => SpeechEngineKind.Voicevox;

    public async Task PrepareAsync(string speaker, CancellationToken cancellationToken)
    {
        var speakerId = ParseSpeakerId(speaker);
        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        // 存在しない speaker だとエンジンは /initialize_speaker で 500 を返し、リトライの分だけ待たされたうえ原因の分からないエンジンエラーになる。
        // /speakers の照会はローカルで数 ms なので、合成の前に確認して入力ミス (InvalidArguments) として即エラーにする。
        EnsureSpeakerExists(await _apiClient.GetSpeakersAsync(cancellationToken), speakerId);
        await _apiClient.InitializeSpeakerAsync(speakerId, cancellationToken);
    }

    public async Task<byte[]> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audioQuery = await _apiClient.GenerateAudioQueryAsync(request, cancellationToken);
        return await _apiClient.SynthesizeAudioAsync(audioQuery, ParseSpeakerId(request.Speaker), cancellationToken);
    }

    public async Task<IReadOnlyList<SpeakerInfo>> GetSpeakersAsync(CancellationToken cancellationToken)
    {
        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        var speakers = await _apiClient.GetSpeakersAsync(cancellationToken);
        return speakers
            .Select(s => new SpeakerInfo(
                s.Name,
                s.Version,
                s.Styles.Select(style => new SpeakerVoice(style.Id.ToString(CultureInfo.InvariantCulture), style.Name)).ToList()))
            .ToList();
    }

    public bool IsEngineUnavailable(Exception ex) => VoiceVoxApiClient.IsEngineUnavailable(ex);

    public void StopIfNotKept() => _engineManager.StopEngineIfNotKept();

    /// <summary>
    /// <c>--speaker</c> の値を検証して正規化する (<c>01</c> → <c>1</c>。キャッシュキーが変わらないように)。
    /// </summary>
    /// <returns>正規化した話者。不正なら null で、理由を <paramref name="error"/> に返す</returns>
    public static string? NormalizeSpeaker(string raw, out string? error)
    {
        if (!Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            error = $"整数ではありません: '{raw}'";
            return null;
        }
        if (value < 0)
        {
            error = $"0 以上である必要があります: {value}";
            return null;
        }
        error = null;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// speaker ID (= スタイル ID) がエンジンの話者一覧に存在しなければ <see cref="ErrorCodes.General.InvalidArguments"/> で失敗させる。
    /// </summary>
    public static void EnsureSpeakerExists(IEnumerable<Speaker> speakers, int speakerId)
    {
        if (speakers.Any(s => s.Styles.Any(style => style.Id == speakerId)))
        {
            return;
        }

        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.General.InvalidArguments,
            message: $"Speaker {speakerId} does not exist in the engine",
            userMessage: $"speaker ID {speakerId} はエンジンに存在しません。",
            suggestedSolution: "`VoicevoxRunCached speakers` で利用可能な ID を確認してください。");
    }

    /// <summary>
    /// 正規化済みの話者 (<see cref="NormalizeSpeaker"/>) を ID にする。
    /// </summary>
    private static int ParseSpeakerId(string speaker)
        => NormalizeSpeaker(speaker, out var error) is { } normalized
            ? Int32.Parse(normalized, CultureInfo.InvariantCulture)
            : throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.General.InvalidArguments,
                message: $"Invalid VOICEVOX speaker: {speaker}",
                userMessage: $"VOICEVOX の話者の指定が不正です。{error}",
                suggestedSolution: "`VoicevoxRunCached speakers` で利用可能な ID を確認してください。");
}
