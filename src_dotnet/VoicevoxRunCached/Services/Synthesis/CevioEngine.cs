using System.Globalization;
using System.Runtime.InteropServices;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// CeVIO AI による <see cref="ISpeechEngine"/>。
/// </summary>
/// <remarks>
/// <para>話者は <c>キャスト名</c> か <c>キャスト名:感情=値,感情=値</c> (値は 0〜100)。<see cref="NormalizeSpeaker"/> が感情を名前順に並べるので、
/// 書き方が違っても同じ指定は同じキャッシュキーになる。感情を書けば書いていない感情は 0、書かなければキャストの既定値で合成する。</para>
/// <para>パラメータの換算 (CeVIO の目盛りは 0〜100、標準は 50):</para>
/// <list type="bullet">
/// <item>話速: <see cref="ToCevioSpeed"/>。CeVIO の Speed は 25 上がるごとに約 2 倍速くなる (実測)</item>
/// <item>音高: <see cref="ToCevioTone"/>。CeVIO の Tone は 0〜100 で約 ±5 半音 (実測)。VOICEVOX の GUI の音高の範囲 (±0.15) を端にそろえる</item>
/// <item>音量: CeVIO の Volume は 50 のまま、合成した波形に <c>--volume</c> を掛ける (<see cref="ApplyGain"/>)。
/// VOICEVOX の volumeScale と同じく振幅の倍率になる (CeVIO の Volume は 0〜100 で約 ±7dB しかなく、0 でも無音にならない)</item>
/// </list>
/// </remarks>
public class CevioEngine : ISpeechEngine
{
    /// <summary>
    /// <see cref="ToCevioTone"/> で Tone の端 (0 / 100) になる音高。
    /// </summary>
    public const double PitchRange = 0.15;

    private readonly CevioHostManager _hostManager;
    private readonly ICevioRemoteService _remote;
    private readonly TimeSpan _timeout;
    // 準備したキャストの感情の既定値。感情を指定しない話者の合成で、前の合成の感情が残らないよう毎回設定する
    private readonly Dictionary<string, IReadOnlyList<CevioComponent>> _castComponents = new(StringComparer.Ordinal);

    public CevioEngine(CevioHostManager hostManager, ICevioRemoteService remote, CevioSettings settings)
    {
        _hostManager = hostManager ?? throw new ArgumentNullException(nameof(hostManager));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        ArgumentNullException.ThrowIfNull(settings);
        _timeout = TimeSpan.FromSeconds(settings.SynthesisTimeoutSeconds);
    }

    public SpeechEngineKind Kind => SpeechEngineKind.Cevio;

    public async Task PrepareAsync(string speaker, CancellationToken cancellationToken)
    {
        var parsed = ParseSpeaker(speaker);
        await _hostManager.EnsureRunningOrThrowAsync(cancellationToken);

        await RunComAsync(() =>
        {
            // 存在しないキャストを Cast に代入すると CeVIO AI がダイアログを出すので、代入する前に一覧で確かめる
            EnsureCastExists(_remote.GetCasts(), parsed.Cast);
            var components = _remote.GetComponents(parsed.Cast);
            EnsureEmotionsExist(components, parsed);
            _castComponents[parsed.Cast] = components;
            return true;
        }, cancellationToken);
    }

    public async Task<byte[]> SynthesizeAsync(VoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var speaker = ParseSpeaker(request.Speaker);
        var defaults = _castComponents.TryGetValue(speaker.Cast, out var components)
            ? components
            : throw new InvalidOperationException($"PrepareAsync を呼ぶ前に合成しようとしました: {speaker.Cast}");
        var talk = new CevioTalk(speaker.Cast, ToCevioSpeed(request.Speed), ToCevioTone(request.Pitch), ResolveComponents(defaults, speaker));
        var gain = (float)Cache.CacheKeyGenerator.NormalizeParameter(request.Volume);

        return await RunComAsync(() => ApplyGain(OutputWave(talk, request.Text), gain), cancellationToken);
    }

    public async Task<IReadOnlyList<SpeakerInfo>> GetSpeakersAsync(CancellationToken cancellationToken)
    {
        await _hostManager.EnsureRunningOrThrowAsync(cancellationToken);
        return await RunComAsync(() => _remote.GetCasts()
            .Select(cast => ToSpeakerInfo(cast, _remote.GetComponents(cast)))
            .ToList(), cancellationToken);
    }

    /// <remarks>
    /// COM の例外・タイムアウトは CeVIO AI との通信の失敗 (途中で閉じられた等) で、文によらないので含める。
    /// </remarks>
    public bool IsEngineUnavailable(Exception ex)
        => ex is VoicevoxRunCachedException { ErrorCode: ErrorCodes.Engine.EngineNotAvailable or ErrorCodes.Api.ApiTimeout } or COMException;

    public void StopIfNotKept() => _hostManager.StopHostIfNotKept();

    /// <summary>
    /// <c>--speaker</c> の値を検証して正規化する (前後の空白を除き、感情を名前順に並べ、値を整数の表記にそろえる)。
    /// キャストと感情が CeVIO AI にあるかは <see cref="PrepareAsync"/> で確かめる。
    /// </summary>
    /// <returns>正規化した話者。不正なら null で、理由を <paramref name="error"/> に返す</returns>
    public static string? NormalizeSpeaker(string raw, out string? error)
    {
        var speaker = TryParseSpeaker(raw, out error);
        return speaker?.ToString();
    }

    /// <summary>
    /// <c>--speed</c> (VOICEVOX の speedScale と同じ倍率) を CeVIO の Speed にする。1.0 → 50、2.0 → 75、0.5 → 25。範囲外は 0 / 100。
    /// </summary>
    public static uint ToCevioSpeed(double speed)
        => ToScale(50 + 25 * Math.Log2(Cache.CacheKeyGenerator.NormalizeParameter(speed)));

    /// <summary>
    /// <c>--pitch</c> を CeVIO の Tone にする。0.0 → 50、±<see cref="PitchRange"/> → 100 / 0。範囲外は 0 / 100。
    /// </summary>
    public static uint ToCevioTone(double pitch)
        => ToScale(50 + Cache.CacheKeyGenerator.NormalizeParameter(pitch) / PitchRange * 50);

    /// <summary>
    /// 16bit PCM の WAV の各サンプルに <paramref name="gain"/> を掛ける。1.0 ならそのまま返す。
    /// </summary>
    /// <remarks>
    /// 1.0 を超えて割れても値が反対側に回り込まないよう、[-1, 1] に収める (<see cref="FillerManager"/> の音量と同じ)。
    /// </remarks>
    public static byte[] ApplyGain(byte[] wavData, float gain)
    {
        if (gain == 1.0f)
        {
            return wavData;
        }

        using var reader = new WaveFileReader(new MemoryStream(wavData));
        var source = reader.ToSampleProvider();
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, reader.WaveFormat))
        {
            var buffer = new float[reader.WaveFormat.SampleRate];
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    buffer[i] = Math.Clamp(buffer[i] * gain, -1f, 1f);
                }
                writer.WriteSamples(buffer, 0, read);
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// キャストが CeVIO AI の一覧に無ければ <see cref="ErrorCodes.General.InvalidArguments"/> で失敗させる。
    /// </summary>
    public static void EnsureCastExists(IReadOnlyList<string> casts, string cast)
    {
        if (casts.Contains(cast, StringComparer.Ordinal))
        {
            return;
        }

        var available = casts.Count == 0 ? "ライセンスのあるキャストがありません" : $"使えるキャスト: {String.Join(" / ", casts)}";
        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.General.InvalidArguments,
            message: $"CeVIO cast does not exist: {cast}",
            userMessage: $"キャスト '{cast}' は CeVIO AI にありません ({available})。",
            suggestedSolution: "`VoicevoxRunCached speakers --engine cevio` で使えるキャストを確認してください。");
    }

    /// <summary>
    /// 指定した感情がキャストに無ければ <see cref="ErrorCodes.General.InvalidArguments"/> で失敗させる。
    /// </summary>
    internal static void EnsureEmotionsExist(IReadOnlyList<CevioComponent> components, CevioSpeaker speaker)
    {
        var missing = speaker.Emotions?.Keys.Where(name => !components.Any(c => c.Name == name)).ToList() ?? [];
        if (missing.Count == 0)
        {
            return;
        }

        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.General.InvalidArguments,
            message: $"CeVIO cast {speaker.Cast} does not have emotions: {String.Join(", ", missing)}",
            userMessage: $"キャスト '{speaker.Cast}' に感情 {String.Join(" / ", missing)} はありません (使える感情: {String.Join(" / ", components.Select(c => c.Name))})。",
            suggestedSolution: "`VoicevoxRunCached speakers --engine cevio` で使える感情を確認してください。");
    }

    /// <summary>
    /// 合成に渡す感情の値。指定があれば書いていない感情は 0、無ければキャストの既定値。
    /// </summary>
    internal static IReadOnlyDictionary<string, uint> ResolveComponents(IReadOnlyList<CevioComponent> defaults, CevioSpeaker speaker)
        => defaults.ToDictionary(
            c => c.Name,
            c => speaker.Emotions is { } emotions ? emotions.GetValueOrDefault(c.Name) : c.Value,
            StringComparer.Ordinal);

    /// <summary>
    /// 正規化済みの話者 (<see cref="NormalizeSpeaker"/>) を分解する。
    /// </summary>
    internal static CevioSpeaker ParseSpeaker(string speaker)
        => TryParseSpeaker(speaker, out var error) ?? throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.General.InvalidArguments,
            message: $"Invalid CeVIO speaker: {speaker}",
            userMessage: $"CeVIO の話者の指定が不正です。{error}",
            suggestedSolution: "`VoicevoxRunCached speakers --engine cevio` で使えるキャストと感情を確認してください。");

    private static CevioSpeaker? TryParseSpeaker(string raw, out string? error)
    {
        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        var cast = (colon < 0 ? raw : raw[..colon]).Trim();
        if (cast.Length == 0)
        {
            error = $"キャスト名がありません: '{raw}'";
            return null;
        }
        if (colon < 0)
        {
            error = null;
            return new CevioSpeaker(cast, null);
        }

        var emotions = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        foreach (var part in raw[(colon + 1)..].Split(','))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var name = (equals < 0 ? part : part[..equals]).Trim();
            if (equals < 0 || name.Length == 0)
            {
                error = $"感情は 感情=値 の形で書いてください: '{part.Trim()}'";
                return null;
            }
            var valueText = part[(equals + 1)..].Trim();
            if (!UInt32.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 100)
            {
                error = $"感情の値は 0〜100 の整数で書いてください: '{part.Trim()}'";
                return null;
            }
            if (!emotions.TryAdd(name, value))
            {
                error = $"感情 '{name}' が重複しています";
                return null;
            }
        }
        error = null;
        return new CevioSpeaker(cast, emotions);
    }

    private static uint ToScale(double value) => (uint)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);

    private static SpeakerInfo ToSpeakerInfo(string cast, IReadOnlyList<CevioComponent> components)
    {
        var defaults = String.Join(", ", components.Select(c => $"{c.Name}={c.Value}"));
        List<SpeakerVoice> voices = [new SpeakerVoice(cast, $"既定 ({defaults})")];
        voices.AddRange(components.Select(c => new SpeakerVoice(new CevioSpeaker(cast, new Dictionary<string, uint> { [c.Name] = 100 }).ToString(), c.Name)));
        return new SpeakerInfo(cast, null, voices);
    }

    /// <summary>
    /// COM の呼び出しをスレッドプールで行い、<see cref="CevioSettings.SynthesisTimeoutSeconds"/> で打ち切る。
    /// </summary>
    /// <remarks>
    /// <para>COM の呼び出しは同期なので、呼び出し元 (読み上げのパイプライン) を止めないようスレッドプールで行う。</para>
    /// <para>CeVIO AI が閉じられた後の Talker の呼び出しは戻らなくなる (実機で確認)。合成の前に <see cref="ICevioRemoteService.IsHostStarted"/> を
    /// 確かめているが、合成の途中で閉じられることもあるので時間で打ち切る。打ち切らないとプロセス間ロックを持ったまま止まり、以後の読み上げがすべて待たされる。
    /// 打ち切った呼び出しのスレッドは戻らないまま残る (バックグラウンドスレッドなのでプロセスの終了は妨げない)。
    /// Ctrl+C も同じ理由で、呼び出しの完了を待たずにキャンセルとして返す。</para>
    /// </remarks>
    private async Task<T> RunComAsync<T>(Func<T> call, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(call, cancellationToken).WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Api.ApiTimeout,
                message: $"CeVIO AI did not respond within {_timeout.TotalSeconds} seconds",
                userMessage: $"CeVIO AI が {_timeout.TotalSeconds} 秒以内に応答しませんでした。",
                innerException: ex,
                suggestedSolution: "CeVIO AI が閉じられていないか確認してください。長い文で時間がかかるなら appsettings.json の Cevio.SynthesisTimeoutSeconds を延ばしてください。");
        }
    }

    /// <summary>
    /// 1 文を一時ファイルに合成して読み込む。
    /// </summary>
    private byte[] OutputWave(CevioTalk talk, string text)
    {
        // 閉じられた後に Talker を呼ぶと戻らなくなるので、先に確かめる (IsHostStarted は閉じられた後もすぐ false を返す)
        if (!_remote.IsHostStarted)
        {
            throw HostClosed();
        }

        var path = Path.Combine(Path.GetTempPath(), $"VoicevoxRunCached.cevio.{Guid.NewGuid():N}.wav");
        try
        {
            if (!_remote.OutputWaveToFile(talk, text, path))
            {
                // CeVIO AI は失敗の理由を返さない。閉じられていればエンジンの問題として残りの文を止める
                throw _remote.IsHostStarted
                    ? new VoicevoxRunCachedException(
                        errorCode: ErrorCodes.Audio.AudioGenerationFailed,
                        message: $"CeVIO AI failed to synthesize: {text}",
                        userMessage: "CeVIO AI で音声を合成できませんでした。")
                    : HostClosed();
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VoicevoxRunCachedException HostClosed() => new(
        errorCode: ErrorCodes.Engine.EngineNotAvailable,
        message: "CeVIO AI host was closed",
        userMessage: "CeVIO AI が閉じられました。",
        suggestedSolution: "CeVIO AI を起動し直してから、もう一度実行してください。");
}

/// <summary>
/// CeVIO の話者 (<see cref="CevioEngine"/> を参照)。
/// </summary>
/// <param name="Cast">キャスト名</param>
/// <param name="Emotions">指定した感情 (名前順)。指定が無ければ null (キャストの既定値)</param>
public record CevioSpeaker(string Cast, IReadOnlyDictionary<string, uint>? Emotions)
{
    /// <summary>
    /// 正規化した表記 (キャッシュキーに入る)。
    /// </summary>
    public override string ToString() => Emotions is null
        ? Cast
        : $"{Cast}:{String.Join(",", Emotions.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value.ToString(CultureInfo.InvariantCulture)}"))}";
}
