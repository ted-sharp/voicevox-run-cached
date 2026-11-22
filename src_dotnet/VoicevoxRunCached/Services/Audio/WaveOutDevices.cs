using System.Text.Json.Serialization;
using NAudio.Wave;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 再生デバイスとして指定する WaveOut デバイス (再生は <c>WaveOut.DeviceNumber = Index</c> で選ぶ)。
/// <c>devices --json</c> でも出すので JsonPropertyName で小文字キーを保持する。
/// </summary>
public sealed record WaveOutDeviceInfo(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// 再生デバイスの指定 (<c>Audio.Devices</c> など) に使う WaveOut のデバイス一覧。
/// </summary>
/// <remarks>
/// <para>WASAPI の一覧とは並び・件数が一致するとは限らないので別に列挙する。</para>
/// <para>WinMM の制約で ProductName は最大 31 文字に切られる。名前での指定もこの名前と照合する (<see cref="OutputDeviceSelector.ToIndex"/>)。</para>
/// </remarks>
public static class WaveOutDevices
{
    public static IReadOnlyList<WaveOutDeviceInfo> List()
        => [.. Enumerable.Range(0, WaveOut.DeviceCount).Select(i => new WaveOutDeviceInfo(i, WaveOut.GetCapabilities(i).ProductName))];
}
