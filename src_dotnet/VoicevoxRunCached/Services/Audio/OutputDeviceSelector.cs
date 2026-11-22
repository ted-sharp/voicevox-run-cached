using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 読み上げを鳴らす再生デバイス (WaveOut の index。-1 は OS 既定) を決める。
/// </summary>
/// <remarks>
/// 優先順は <c>--device</c> → <see cref="AudioSettings.SpeakerDevices"/> の最初に合う項目 → <see cref="AudioSettings.OutputDevice"/>。
/// 話者はキャラクターで照合し、VOICEPEAK・CeVIO の感情 (<c>:</c> から後ろ) は見ない。感情を変えるたびに対応表を書かずに済むように。
/// VOICEVOX の話者はスタイル ID なので、スタイルごとに書く。
/// </remarks>
public static class OutputDeviceSelector
{
    /// <param name="settings">Audio の設定</param>
    /// <param name="request">読み上げの要求 (<see cref="VoiceRequest.Speaker"/> は正規化済み)</param>
    /// <param name="deviceOverride"><c>--device</c> の値</param>
    public static int Resolve(AudioSettings settings, VoiceRequest request, int? deviceOverride)
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

    private static string CharacterOf(string normalizedSpeaker)
    {
        var separator = normalizedSpeaker.IndexOf(':');
        return separator < 0 ? normalizedSpeaker : normalizedSpeaker[..separator];
    }
}
