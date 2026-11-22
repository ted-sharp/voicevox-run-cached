using System.Globalization;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 読み上げを鳴らす再生デバイスを決める。指定は WaveOut の index (-1 は OS 既定) か、デバイス名の一部。
/// </summary>
/// <remarks>
/// <para>優先順は <c>--device</c> → <see cref="AudioSettings.SpeakerDevices"/> の最初に合う項目 → <see cref="AudioSettings.OutputDevice"/>。
/// 話者はキャラクターで照合し、VOICEPEAK・CeVIO の感情 (<c>:</c> から後ろ) は見ない。感情を変えるたびに対応表を書かずに済むように。
/// VOICEVOX の話者はスタイル ID なので、スタイルごとに書く。</para>
/// <para>名前でも指定できるのは、index がつながっているデバイスだけに振られ、USB / Bluetooth の抜き差しでずれるため。
/// 名前は WaveOut の名前 (WinMM の制約で 31 文字に切られたもの) と照合する。</para>
/// </remarks>
public static class OutputDeviceSelector
{
    /// <summary>
    /// 指定を書いた場所 (設定・<c>--device</c>) から、使う指定の文字列を選ぶ。どのデバイスかは <see cref="ToIndex"/> で決める。
    /// </summary>
    /// <param name="settings">Audio の設定</param>
    /// <param name="request">読み上げの要求 (<see cref="VoiceRequest.Speaker"/> は正規化済み)</param>
    /// <param name="deviceOverride"><c>--device</c> の値</param>
    public static string Resolve(AudioSettings settings, VoiceRequest request, string? deviceOverride)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        if (deviceOverride is { } device)
        {
            return device;
        }

        var character = CharacterOf(request.Speaker);
        foreach (var entry in settings.SpeakerDevices)
        {
            // 書式は設定の検証で確かめてある
            if (entry.Engine == request.Engine
                && SpeechEngineProvider.NormalizeSpeaker(entry.Engine, entry.Speaker, out _) is { } normalized
                && CharacterOf(normalized) == character)
            {
                return entry.Device;
            }
        }
        return settings.OutputDevice;
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
    /// 指定 <paramref name="spec"/> を WaveOut の index にする。見つからなければ <paramref name="fallbacks"/> を順に探す。
    /// </summary>
    /// <param name="spec">index (-1 は OS 既定) か、デバイス名の一部 (大文字小文字を区別しない)</param>
    /// <param name="fallbacks">代わりのデバイス (<see cref="AudioSettings.FallbackDevices"/>。書き方は <paramref name="spec"/> と同じ)</param>
    /// <param name="waveOutNames">WaveOut のデバイス名 (要素の位置が index)</param>
    /// <exception cref="VoicevoxRunCachedException">
    /// どれも見つからない (<see cref="ErrorCodes.Audio.OutputDeviceNotFound"/>)・
    /// 名前が複数のデバイスに合う (<see cref="ErrorCodes.Audio.OutputDeviceAmbiguous"/>)
    /// </exception>
    /// <remarks>
    /// <para>名前全体が一致するデバイスを部分一致より優先する。同じ語を含むデバイスが複数あっても、名前全体を書けば選べるように。</para>
    /// <para>複数に合うのは書き方の問題 (どれを鳴らすか決められない) なので、次の候補に進まずに投げる。</para>
    /// <para>-1 は常にあるので、<paramref name="fallbacks"/> の最後に書けば、どれも無いとき OS 既定で鳴る (エラーにならない)。</para>
    /// <para>確かめずに無い index を渡すと、合成した後の waveOutOpen で想定外のエラー (スタックトレース付きの終了コード 1) になるため、
    /// 呼び出し元はロックを待つ前に呼ぶ。</para>
    /// </remarks>
    public static DeviceChoice ToIndex(string spec, IReadOnlyList<string> fallbacks, IReadOnlyList<string> waveOutNames)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(fallbacks);
        ArgumentNullException.ThrowIfNull(waveOutNames);

        foreach (var candidate in (IEnumerable<string>)[spec, .. fallbacks])
        {
            if (Find(candidate, waveOutNames) is { } index)
            {
                return new DeviceChoice(index, candidate);
            }
        }

        var available = waveOutNames.Count == 0
            ? "-1 (OS 既定) だけ"
            : $"-1 (OS 既定) と {DescribeDevices(waveOutNames, Enumerable.Range(0, waveOutNames.Count))}";
        var reason = fallbacks.Count == 0
            ? $"再生デバイス {DescribeSpec(spec)} が見つかりません"
            : $"再生デバイス {DescribeSpec(spec)} も、代わりのデバイス ({String.Join(", ", fallbacks.Select(DescribeSpec))}) も見つかりません";
        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.Audio.OutputDeviceNotFound,
            message: $"Output device not found: {spec} (fallbacks: {String.Join(", ", fallbacks)})",
            userMessage: $"{reason} (つながっているのは {available})。",
            suggestedSolution: "devices --speak で確かめてください。番号は USB や Bluetooth のデバイスを抜き差しすると変わることがあるので、名前の一部で書くとずれません。"
                + "見つからないときに鳴らすデバイスは Audio.FallbackDevices に順に書けます (最後に -1 を書くと OS 既定)。");
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
/// <param name="UsedSpec">使った指定 (指定のデバイスが見つからず代わりのデバイスにしたときは、その代わりの指定)</param>
public sealed record DeviceChoice(int Index, string UsedSpec);
