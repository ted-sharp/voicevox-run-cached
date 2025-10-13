using System.Text.Json;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Utilities;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// デバイス関連のコマンド処理を行うクラス
/// </summary>
public class DeviceCommandHandler
{
    private readonly ILogger _logger;

    public DeviceCommandHandler(AppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 利用可能なオーディオデバイスの一覧を取得・表示します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>処理結果の終了コード</returns>
    public int HandleDevices(string[] args)
    {
        try
        {
            bool outputJson = args.Contains("--json");
            bool full = args.Contains("--full");

            using var enumerator = new MMDeviceEnumerator();

            var (defaultName, defaultId) = GetDefaultAudioEndpoint(enumerator);
            var deviceList = full ? EnumerateDevices(enumerator) : new List<object>();

            if (outputJson)
            {
                OutputJson(defaultName, defaultId, deviceList);
                return 0;
            }

            OutputConsole(defaultName, defaultId, deviceList, full);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error listing devices: {ex.Message}", _logger);
            return 1;
        }
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
            ConsoleHelper.WriteWarning($"Failed to get default audio endpoint: {ex.Message}", _logger);
            return (null, null);
        }
    }

    private List<object> EnumerateDevices(MMDeviceEnumerator enumerator)
    {
        var list = new List<object>();
        try
        {
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
            {
                try
                {
                    list.Add(new
                    {
                        id = d.ID,
                        name = d.FriendlyName,
                        state = d.State.ToString()
                    });
                }
                catch (Exception inner)
                {
                    ConsoleHelper.WriteWarning($"Failed to read device info: {inner.Message}", _logger);
                }
            }
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteWarning($"Failed to enumerate audio endpoints: {ex.Message}", _logger);
        }
        return list;
    }

    private static void OutputJson(string? defaultName, string? defaultId, List<object> deviceList)
    {
        var payload = new
        {
            @default = new { id = defaultId, name = defaultName },
            devices = deviceList
        };
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptionsCache.Indented);
        Console.WriteLine(json);
    }

    private static void OutputConsole(string? defaultName, string? defaultId, List<object> deviceList, bool full)
    {
        Console.WriteLine("Available output devices (WASAPI):");

        if (!String.IsNullOrEmpty(defaultName))
        {
            Console.WriteLine($"  Default: \"{defaultName}\" (ID: {defaultId})");
        }
        else
        {
            Console.WriteLine("  Default: (unavailable)");
        }

        if (full)
        {
            OutputDeviceList(deviceList);
        }
        else
        {
            Console.WriteLine("  (use 'devices --full' for a detailed list)");
        }
    }

    private static void OutputDeviceList(List<object> deviceList)
    {
        if (deviceList.Count == 0)
        {
            Console.WriteLine("  (no active render devices)");
            return;
        }

        int idx = 0;
        foreach (var item in deviceList)
        {
            var id = (string?)item.GetType().GetProperty("id")?.GetValue(item) ?? "";
            var name = (string?)item.GetType().GetProperty("name")?.GetValue(item) ?? "";
            var state = (string?)item.GetType().GetProperty("state")?.GetValue(item) ?? "";
            Console.WriteLine($"  [{idx}] \"{name}\" (ID: {id}) State: {state}");
            idx++;
        }
    }
}
