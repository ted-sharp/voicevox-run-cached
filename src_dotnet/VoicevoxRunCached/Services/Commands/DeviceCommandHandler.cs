using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;
using NAudio.CoreAudioApi;
using System.Text.Json;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// デバイス関連のコマンド処理を行うクラス
/// </summary>
public class DeviceCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public DeviceCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            // デフォルトエンドポイント（環境によっては例外が発生する可能性）
            string? defaultName = null;
            string? defaultId = null;
            try
            {
                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultName = def?.FriendlyName ?? "Default Device";
                defaultId = def?.ID ?? "";
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteWarning($"Failed to get default audio endpoint: {ex.Message}", this._logger);
            }

            var list = new List<object>();
            if (full)
            {
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
                            ConsoleHelper.WriteWarning($"Failed to read device info: {inner.Message}", this._logger);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteWarning($"Failed to enumerate audio endpoints: {ex.Message}", this._logger);
                }
            }

            if (outputJson)
            {
                var payload = new
                {
                    @default = new { id = defaultId, name = defaultName },
                    devices = list
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                return 0;
            }

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
                if (list.Count == 0)
                {
                    Console.WriteLine("  (no active render devices)");
                }
                else
                {
                    int idx = 0;
                    foreach (var item in list)
                    {
                        var id = (string?)item.GetType().GetProperty("id")?.GetValue(item) ?? "";
                        var name = (string?)item.GetType().GetProperty("name")?.GetValue(item) ?? "";
                        var state = (string?)item.GetType().GetProperty("state")?.GetValue(item) ?? "";
                        Console.WriteLine($"  [{idx}] \"{name}\" (ID: {id}) State: {state}");
                        idx++;
                    }
                }
            }
            else
            {
                Console.WriteLine("  (use 'devices --full' for a detailed list)");
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error listing devices: {ex.Message}", this._logger);
            return 1;
        }
    }
}
