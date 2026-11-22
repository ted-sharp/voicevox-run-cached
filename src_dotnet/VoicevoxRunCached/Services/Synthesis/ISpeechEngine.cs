using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// 合成エンジン (VOICEVOX 等)。読み上げ・<c>--init</c>・<c>speakers</c> はこれを通してエンジンを使う。
/// </summary>
/// <remarks>
/// <para>呼び出しはすべて <see cref="SpeechLock"/> の中から 1 件ずつ行う (並行して呼ばない)。</para>
/// <para>話者 (<c>speaker</c>) は <see cref="SpeechEngineProvider.NormalizeSpeaker"/> で正規化した文字列。</para>
/// </remarks>
public interface ISpeechEngine
{
    SpeechEngineKind Kind { get; }

    /// <summary>
    /// 合成の準備: エンジンの起動確認 (止まっていれば設定に従って起動)・話者の存在確認・話者の初期化。
    /// 読み上げでは最初に合成が必要になったときに 1 回だけ呼ぶ。
    /// </summary>
    /// <exception cref="Exceptions.VoicevoxRunCachedException">
    /// エンジンを使えない (<see cref="Exceptions.ErrorCodes.Engine.EngineNotAvailable"/>)・話者がいない (<see cref="Exceptions.ErrorCodes.General.InvalidArguments"/>)
    /// </exception>
    Task PrepareAsync(string speaker, CancellationToken cancellationToken);

    /// <summary>
    /// 1 文を合成して WAV を返す。<see cref="PrepareAsync"/> の後に呼ぶ。
    /// </summary>
    /// <remarks>
    /// 話速・音高・音量は <see cref="Cache.CacheKeyGenerator.NormalizeParameter"/> で丸めた値で合成する (キャッシュキーと一致させる)。
    /// </remarks>
    Task<byte[]> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 話者の一覧 (<c>speakers</c>)。エンジンが止まっていれば設定に従って起動する。
    /// </summary>
    Task<IReadOnlyList<SpeakerInfo>> GetSpeakersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// エンジンそのものに届かない失敗か。true なら、残りの文の合成 (読み上げ・<c>--init</c>) を中止する
    /// (1 件ごとにタイムアウトを待つと、プロセス間ロックを持ったまま後続を待たせるため)。
    /// 特定の文だけで起きうる失敗は含めない。
    /// </summary>
    bool IsEngineUnavailable(Exception ex);

    /// <summary>
    /// このプロセスが起動したエンジンを、設定で常駐させないなら止める。<see cref="SpeechLock"/> がロックを解放する前に呼ぶ。
    /// </summary>
    void StopIfNotKept();

    /// <summary>
    /// このプロセスがエンジンへの接続を持っていて、プロセスが終わるまで手放せないか。
    /// true なら、読み上げは再生が終わるまで <see cref="SpeechLock"/> を手放さない (<see cref="TextToSpeechProcessor"/>)。
    /// </summary>
    bool HoldsConnectionUntilExit { get; }

    /// <summary>
    /// このエンジンのフィラーの文と回数 (エンジンのセクションの <c>FillerTexts</c> / <c>MaxFillersPerWait</c>)。
    /// </summary>
    IEngineFillerSettings FillerSettings { get; }
}

/// <summary>
/// <c>speakers</c> に出す話者 (VOICEVOX ではキャラクター)。
/// </summary>
/// <param name="Name">名前</param>
/// <param name="Version">バージョン。無ければ null</param>
/// <param name="Voices">選べる声 (VOICEVOX ではスタイル)</param>
public record SpeakerInfo(string Name, string? Version, IReadOnlyList<SpeakerVoice> Voices);

/// <param name="Id"><c>--speaker</c> に渡す値</param>
/// <param name="Name">名前</param>
public record SpeakerVoice(string Id, string Name);
