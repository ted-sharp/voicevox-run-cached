using System.Globalization;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 読み上げを鳴らす再生デバイスを決める。候補を上から順に探し、つながっている最初のデバイスで鳴らす。
/// 候補の各要素は WaveOut の index (-1 は OS 既定) か、デバイス名の一部。
/// </summary>
/// <remarks>
/// <para>候補は <c>--device</c> (指定が無ければ <see cref="AudioSettings.SpeakerDevices"/> の最初に合う項目の候補) に、
/// <see cref="AudioSettings.Devices"/> (空なら -1) を続けたもの。
/// 代わりのデバイスを話者ごとに書かずに済むよう、話者の候補の後に全体の候補を続ける。</para>
/// <para>話者はキャラクターで照合し、VOICEPEAK・CeVIO の感情 (<c>:</c> から後ろ) は見ない。感情を変えるたびに対応表を書かずに済むように。
/// VOICEVOX の話者はスタイル ID なので、スタイルごとに書く。</para>
/// <para>名前でも指定できるのは、index がつながっているデバイスだけに振られ、USB / Bluetooth の抜き差しでずれるため。
/// 名前は WaveOut の名前 (WinMM の制約で 31 文字に切られたもの) と照合する。</para>
/// </remarks>
public static class OutputDeviceSelector
{
    /// <summary>
    /// 探す順の候補を組み立てる。どのデバイスかは <see cref="ToIndex"/> で決める。
    /// </summary>
    /// <param name="settings">Audio の設定</param>
    /// <param name="request">読み上げの要求 (<see cref="VoiceRequest.Speaker"/> は正規化済み)</param>
    /// <param name="deviceOverride"><c>--device</c> の値</param>
    public static IReadOnlyList<string> Resolve(AudioSettings settings, VoiceRequest request, string? deviceOverride)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<string> overall = settings.Devices.Length > 0 ? settings.Devices : [OsDefault];
        var first = deviceOverride is { } device ? [device] : SpeakerCandidates(settings, request);
        return [.. first, .. overall];
    }

    private const string OsDefault = "-1";

    private static IReadOnlyList<string> SpeakerCandidates(AudioSettings settings, VoiceRequest request)
    {
        var character = CharacterOf(request.Speaker);
        foreach (var entry in settings.SpeakerDevices)
        {
            // 書式は設定の検証で確かめてある
            if (entry.Engine == request.Engine
                && SpeechEngineProvider.NormalizeSpeaker(entry.Engine, entry.Speaker, out _) is { } normalized
                && CharacterOf(normalized) == character)
            {
                return entry.Devices;
            }
        }
        return [];
    }

    /// <summary>
    /// 指定の書き方を確かめる (設定の検証と <c>--device</c> で共通)。空は不正、整数なら -1 以上。
    /// </summary>
    /// <remarks>
    /// 名前はここでは照合しない。デバイスは設定を書いた後に挿されることがあるため (照合は鳴らす直前の <see cref="ToIndex"/>)。
    /// </remarks>
    public static bool TryValidateSpec(string? spec, out string? error)
    {
        if (String.IsNullOrWhiteSpace(spec))
        {
            error = "番号か、デバイス名の一部を指定してください";
            return false;
        }
        if (TryParseIndex(spec, out var index) && index < -1)
        {
            error = $"番号は -1 以上である必要があります: {index}";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 候補 <paramref name="candidates"/> を上から順に探し、最初に見つかったデバイスの WaveOut の index を返す。
    /// </summary>
    /// <param name="candidates">候補 (<see cref="Resolve"/>)。各要素は index (-1 は OS 既定) か、デバイス名の一部 (大文字小文字を区別しない)</param>
    /// <param name="waveOutNames">WaveOut のデバイス名 (要素の位置が index)</param>
    /// <exception cref="VoicevoxRunCachedException">
    /// どれも見つからない (<see cref="ErrorCodes.Audio.OutputDeviceNotFound"/>)・
    /// 名前が複数のデバイスに合う (<see cref="ErrorCodes.Audio.OutputDeviceAmbiguous"/>)
    /// </exception>
    /// <remarks>
    /// <para>名前全体が一致するデバイスを部分一致より優先する。同じ語を含むデバイスが複数あっても、名前全体を書けば選べるように。</para>
    /// <para>複数に合うのは書き方の問題 (どれを鳴らすか決められない) なので、次の候補に進まずに投げる。</para>
    /// <para>-1 は常にあるので、候補の最後に書けば、どれも無いとき OS 既定で鳴る (エラーにならない)。</para>
    /// <para>確かめずに無い index を渡すと、合成した後の waveOutOpen で想定外のエラー (スタックトレース付きの終了コード 1) になるため、
    /// 呼び出し元はロックを待つ前に呼ぶ。</para>
    /// </remarks>
    public static DeviceChoice ToIndex(IReadOnlyList<string> candidates, IReadOnlyList<string> waveOutNames)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(waveOutNames);

        for (var i = 0; i < candidates.Count; i++)
        {
            if (Find(candidates[i], waveOutNames) is { } index)
            {
                return new DeviceChoice(index, candidates[i], IsFirstCandidate: i == 0);
            }
        }

        var available = waveOutNames.Count == 0
            ? "-1 (OS 既定) だけ"
            : $"-1 (OS 既定) と {DescribeDevices(waveOutNames, Enumerable.Range(0, waveOutNames.Count))}";
        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.Audio.OutputDeviceNotFound,
            message: $"Output device not found: {String.Join(", ", candidates)}",
            userMessage: $"再生デバイス ({String.Join(", ", candidates.Select(DescribeSpec))}) が見つかりません (つながっているのは {available})。",
            suggestedSolution: "devices --speak で確かめてください。番号は USB や Bluetooth のデバイスを抜き差しすると変わることがあるので、名前の一部で書くとずれません。"
                + "Audio.Devices の最後に -1 を書くと、どれも無いとき OS 既定で鳴ります。");
    }

    /// <summary>
    /// <paramref name="spec"/> に合うデバイスの index。無ければ null、複数に合えば投げる。
    /// </summary>
    private static int? Find(string spec, IReadOnlyList<string> waveOutNames)
    {
        if (TryParseIndex(spec, out var index))
        {
            return index < waveOutNames.Count ? index : null;
        }

        var name = spec.Trim();
        var exact = IndicesWhere(waveOutNames, n => String.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = IndicesWhere(waveOutNames, n => n.Contains(name, StringComparison.OrdinalIgnoreCase));
        return partial.Count switch
        {
            1 => partial[0],
            0 => null,
            _ => throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Audio.OutputDeviceAmbiguous,
                message: $"Output device name \"{name}\" matches {partial.Count} devices",
                userMessage: $"\"{name}\" に合う再生デバイスが複数あります: {DescribeDevices(waveOutNames, partial)}",
                suggestedSolution: "1 台だけに合うように名前を長く書くか、名前全体か番号で指定してください。"),
        };
    }

    /// <summary>
    /// 指定を利用者向けに書く (<c>-1 (OS 既定)</c> / <c>2 番</c> / <c>"Jabra"</c>)。
    /// </summary>
    public static string DescribeSpec(string spec)
        => TryParseIndex(spec, out var index) ? (index < 0 ? "-1 (OS 既定)" : $"{index} 番") : $"\"{spec.Trim()}\"";

    private static bool TryParseIndex(string spec, out int index)
        => Int32.TryParse(spec.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);

    private static List<int> IndicesWhere(IReadOnlyList<string> names, Func<string, bool> predicate)
        => [.. Enumerable.Range(0, names.Count).Where(i => predicate(names[i]))];

    private static string DescribeDevices(IReadOnlyList<string> names, IEnumerable<int> indices)
        => String.Join(", ", indices.Select(i => $"{i}: \"{names[i]}\""));

    private static string CharacterOf(string normalizedSpeaker)
    {
        var separator = normalizedSpeaker.IndexOf(':');
        return separator < 0 ? normalizedSpeaker : normalizedSpeaker[..separator];
    }
}

/// <summary>
/// <see cref="OutputDeviceSelector.ToIndex"/> の結果。
/// </summary>
/// <param name="Index">WaveOut の index (-1 は OS 既定)</param>
/// <param name="UsedSpec">使った候補</param>
/// <param name="IsFirstCandidate">最初の候補か (false なら、前の候補が見つからず代わりのデバイスにした)</param>
public sealed record DeviceChoice(int Index, string UsedSpec, bool IsFirstCandidate);
