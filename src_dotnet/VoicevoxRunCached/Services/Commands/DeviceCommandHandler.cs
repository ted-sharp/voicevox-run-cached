using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// devices コマンドが返す 1 つのオーディオデバイス情報。
/// JSON 出力でも使うので JsonPropertyName で小文字キーを保持する。
/// </summary>
public sealed record AudioDeviceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// Audio.OutputDevice に指定する WaveOut デバイス (再生は WaveOut.DeviceNumber = Index で選ぶ)。
/// </summary>
public sealed record WaveOutDeviceInfo(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// devices --json コマンドの出力ペイロード形状。
/// </summary>
internal sealed record AudioDeviceListPayload(
    [property: JsonPropertyName("default")] AudioDeviceInfo Default,
    [property: JsonPropertyName("devices")] IReadOnlyList<AudioDeviceInfo> Devices,
    [property: JsonPropertyName("waveOutDevices")] IReadOnlyList<WaveOutDeviceInfo> WaveOutDevices);

/// <summary>
/// デバイス関連のコマンド処理を行うクラス
/// </summary>
public class DeviceCommandHandler
{
    private readonly ILogger _logger;

    public DeviceCommandHandler(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="outputJson"><c>--json</c></param>
    /// <param name="full"><c>--full</c></param>
    public int HandleDevices(bool outputJson, bool full)
    {
        using var enumerator = new MMDeviceEnumerator();

        var (defaultName, defaultId) = GetDefaultAudioEndpoint(enumerator);
        var deviceList = full ? EnumerateDevices(enumerator) : new List<AudioDeviceInfo>();
        var waveOutDevices = EnumerateWaveOutDevices();

        if (outputJson)
        {
            OutputJson(defaultName, defaultId, deviceList, waveOutDevices);
            return 0;
        }

        OutputConsole(defaultName, defaultId, deviceList, full);
        OutputWaveOutDevices(waveOutDevices);
        return 0;
    }

    /// <summary>
    /// 再生デバイスの選択 (Audio.OutputDevice) に使う WaveOut の index 一覧。
    /// WASAPI の一覧とは並び・件数が一致するとは限らないので別に列挙する。
    /// </summary>
    /// <remarks>
    /// WinMM の制約で ProductName は最大 31 文字に切られる。
    /// </remarks>
    private static List<WaveOutDeviceInfo> EnumerateWaveOutDevices()
    {
        return Enumerable.Range(0, WaveOut.DeviceCount)
            .Select(i => new WaveOutDeviceInfo(i, WaveOut.GetCapabilities(i).ProductName))
            .ToList();
    }

    private (string? Name, string? Id) GetDefaultAudioEndpoint(MMDeviceEnumerator enumerator)
    {
        try
        {
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return (def?.FriendlyName ?? "Default Device", def?.ID ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "デフォルトオーディオデバイスの取得に失敗しました");
            return (null, null);
        }
    }

    private List<AudioDeviceInfo> EnumerateDevices(MMDeviceEnumerator enumerator)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
            {
                try
                {
                    list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.State.ToString()));
                }
                catch (Exception inner)
                {
                    _logger.LogWarning(inner, "デバイス情報の読み取りに失敗しました");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "オーディオデバイスの列挙に失敗しました");
        }
        return list;
    }

    private static void OutputJson(
        string? defaultName, string? defaultId, IReadOnlyList<AudioDeviceInfo> deviceList, IReadOnlyList<WaveOutDeviceInfo> waveOutDevices)
    {
        var payload = new AudioDeviceListPayload(
            new AudioDeviceInfo(defaultId ?? "", defaultName ?? "", ""),
            deviceList,
            waveOutDevices);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptionsCache.Indented);
        Console.WriteLine(json);
    }

    private static void OutputConsole(string? defaultName, string? defaultId, IReadOnlyList<AudioDeviceInfo> deviceList, bool full)
    {
        ConsoleHelper.WriteLine("Available output devices (WASAPI):");

        if (!String.IsNullOrEmpty(defaultName))
        {
            ConsoleHelper.WriteLine($"  Default: \"{defaultName}\" (ID: {defaultId})");
        }
        else
        {
            ConsoleHelper.WriteLine("  Default: (unavailable)");
        }

        if (full)
        {
            OutputDeviceList(deviceList);
        }
        else
        {
            ConsoleHelper.WriteLine("  (use 'devices --full' for a detailed list)");
        }
    }

    private static void OutputDeviceList(IReadOnlyList<AudioDeviceInfo> deviceList)
    {
        if (deviceList.Count == 0)
        {
            ConsoleHelper.WriteLine("  (no active render devices)");
            return;
        }

        for (int idx = 0; idx < deviceList.Count; idx++)
        {
            var d = deviceList[idx];
            ConsoleHelper.WriteLine($"  [{idx}] \"{d.Name}\" (ID: {d.Id}) State: {d.State}");
        }
    }

    private static void OutputWaveOutDevices(IReadOnlyList<WaveOutDeviceInfo> waveOutDevices)
    {
        ConsoleHelper.WriteLine("");
        ConsoleHelper.WriteLine("Audio.OutputDevice (WaveOut index):");
        ConsoleHelper.WriteLine("  -1: OS default");
        foreach (var d in waveOutDevices)
        {
            ConsoleHelper.WriteLine($"  {d.Index,2}: \"{d.Name}\"");
        }
    }
}
