using System.Collections;
using System.Runtime.InteropServices;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// CeVIO AI の外部連携インターフェイス (COM の <c>CeVIO.Talk.RemoteService2</c>)。COM に触れるのはこの実装だけにする。
/// </summary>
/// <remarks>
/// <para>呼び出しは同期で、CeVIO AI の処理が終わるまで戻らない (キャンセルできない)。</para>
/// <para>存在しないキャストを <c>Cast</c> に代入すると、CeVIO AI が「ライセンスされていないキャスト」のダイアログを画面に出す
/// (COM 側は例外にならない)。<see cref="GetComponents"/> / <see cref="OutputWaveToFile"/> には <see cref="GetCasts"/> にあるキャストだけを渡す。</para>
/// </remarks>
public interface ICevioRemoteService
{
    /// <summary>
    /// CeVIO AI が起動していて、外部から使える状態か。
    /// </summary>
    /// <exception cref="VoicevoxRunCachedException">CeVIO AI がインストールされていない (<see cref="ErrorCodes.Engine.EngineNotAvailable"/>)</exception>
    bool IsHostStarted { get; }

    /// <summary>
    /// CeVIO AI を閉じる。
    /// </summary>
    void CloseHost();

    /// <summary>
    /// 使えるキャスト (ライセンスのあるもの) の名前。
    /// </summary>
    IReadOnlyList<string> GetCasts();

    /// <summary>
    /// キャストの感情と、その既定値 (0〜100)。
    /// </summary>
    IReadOnlyList<CevioComponent> GetComponents(string cast);

    /// <summary>
    /// 1 文を合成して <paramref name="path"/> に WAV (48kHz mono 16bit) で書く。
    /// </summary>
    /// <returns>CeVIO AI が失敗を返したら false (空の文・書き込めない保存先・CeVIO AI が閉じられた等。理由は返らない)</returns>
    bool OutputWaveToFile(CevioTalk talk, string text, string path);
}

/// <param name="Name">感情の名前 (嬉しい・普通 等)</param>
/// <param name="Value">値 (0〜100)</param>
public record CevioComponent(string Name, uint Value);

/// <summary>
/// 合成のパラメータ。値は CeVIO の目盛り (0〜100、中央の 50 が標準)。
/// </summary>
/// <param name="Cast">キャスト名 (<see cref="ICevioRemoteService.GetCasts"/> にあるもの)</param>
/// <param name="Speed">話速</param>
/// <param name="Tone">音の高さ</param>
/// <param name="Components">感情ごとの値。キャストのすべての感情を含める (前の合成の値が残らないように)</param>
public record CevioTalk(string Cast, uint Speed, uint Tone, IReadOnlyDictionary<string, uint> Components);

/// <summary>
/// <see cref="ICevioRemoteService"/> の実装 (COM の遅延バインディング)。
/// </summary>
/// <remarks>
/// <para>COM サーバーは .NET Framework のアセンブリ (InprocServer32 = mscoree) で、.NET 11 の 64bit プロセスにそのまま読み込める。
/// ThreadingModel は Both なので、スレッドプール (MTA) のどのスレッドから呼んでもよい。</para>
/// <para>ホストの起動 (<c>ServiceControl2V40.StartHost</c>) は使わない。StartHost が起動した CeVIO AI は本ツールの継承可能なハンドル
/// (Hooks の stdout / stderr のパイプ) を引き継ぎ、常駐させると Hooks がタイムアウトまで待たされる。起動は <see cref="CevioHostManager"/> が行う。</para>
/// <para>型付きの相互運用 (タイプライブラリからの参照) にしないのは、CeVIO AI が無い環境でもビルド・起動できるようにするため。</para>
/// </remarks>
public sealed class CevioRemoteService : ICevioRemoteService
{
    private const string ServiceControlProgId = "CeVIO.Talk.RemoteService2.ServiceControl2V40";
    private const string TalkerProgId = "CeVIO.Talk.RemoteService2.Talker2V40";

    /// <summary>
    /// CeVIO の音量。<c>--volume</c> は合成後の波形に掛けるので、ここは標準 (50) に固定する。
    /// </summary>
    private const uint NeutralVolume = 50;

    private dynamic? _serviceControl;
    // 合成に使い回す Talker。キャスト・パラメータ・感情は合成のたびにすべて設定し直す
    private dynamic? _talker;

    public bool IsHostStarted => (bool)ServiceControl.IsHostStarted;

    /// <remarks>
    /// モード 0 (Default) は、CeVIO AI で編集中の内容があれば保存の確認を出す (利用者の編集を黙って捨てない)。
    /// </remarks>
    public void CloseHost() => ServiceControl.CloseHost(0);

    public IReadOnlyList<string> GetCasts()
    {
        dynamic talker = Create(TalkerProgId);
        dynamic casts = talker.AvailableCasts;
        var names = new List<string>();
        for (var i = 0; i < (int)casts.Length; i++)
        {
            names.Add((string)casts.At(i));
        }
        return names;
    }

    public IReadOnlyList<CevioComponent> GetComponents(string cast)
    {
        // 新しい Talker で、キャストの既定値を読む
        dynamic talker = Create(TalkerProgId);
        talker.Cast = cast;
        return EnumerateComponents((object)talker)
            .Select(component => new CevioComponent((string)component.Name, (uint)component.Value))
            .ToList();
    }

    public bool OutputWaveToFile(CevioTalk talk, string text, string path)
    {
        ArgumentNullException.ThrowIfNull(talk);
        dynamic talker = _talker ??= Create(TalkerProgId);
        talker.Cast = talk.Cast;
        talker.Volume = NeutralVolume;
        talker.Speed = talk.Speed;
        talker.Tone = talk.Tone;
        foreach (var component in EnumerateComponents((object)talker))
        {
            component.Value = talk.Components[(string)component.Name];
        }
        return (bool)talker.OutputWaveToFile(text, path);
    }

    private dynamic ServiceControl => _serviceControl ??= Create(ServiceControlProgId);

    /// <remarks>
    /// <c>Components</c> の既定のディスパッチは IEnumerable で、<c>Length</c> / <c>At</c> を遅延バインディングで呼べない。列挙して取り出す。
    /// </remarks>
    private static IEnumerable<dynamic> EnumerateComponents(object talker)
    {
        foreach (dynamic component in (IEnumerable)((dynamic)talker).Components)
        {
            yield return component;
        }
    }

    private static object Create(string progId)
    {
        // ProgID が無い = CeVIO AI がインストールされていない
        var type = Type.GetTypeFromProgID(progId) ?? throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.Engine.EngineNotAvailable,
            message: $"COM class {progId} is not registered",
            userMessage: NotInstalledMessage,
            suggestedSolution: NotInstalledSolution);
        try
        {
            return Activator.CreateInstance(type)!;
        }
        catch (COMException ex)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Engine.EngineNotAvailable,
                message: $"Failed to create COM object {progId}",
                userMessage: NotInstalledMessage,
                innerException: ex,
                suggestedSolution: NotInstalledSolution);
        }
    }

    private const string NotInstalledMessage = "CeVIO AI を使えません。";
    private const string NotInstalledSolution = "CeVIO AI がインストールされているか確認してください。";
}
