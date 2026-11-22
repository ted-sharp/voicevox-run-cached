using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
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

    /// <summary>
    /// OS 既定の再生デバイスの名前 (-1 で鳴るデバイス)。取れなければ null。
    /// </summary>
    /// <remarks>
    /// WaveOut の -1 (WAVE_MAPPER) は index を持たず名前も取れないので、Core Audio の既定のエンドポイントから取る。
    /// 名前は切られていない (WaveOut の名前はこの先頭 31 文字)。
    /// 再生デバイスが 1 つも無いときなどは COMException になる。除外を確かめられないだけなので null を返す。
    /// </remarks>
    public static string? OsDefaultName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
