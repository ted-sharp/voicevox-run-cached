using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
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
    private readonly TextToSpeechProcessor _ttsProcessor;
    private readonly ILogger _logger;

    public DeviceCommandHandler(TextToSpeechProcessor ttsProcessor, ILogger logger)
    {
        _ttsProcessor = ttsProcessor ?? throw new ArgumentNullException(nameof(ttsProcessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> HandleDevicesAsync(DevicesOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var waveOutDevices = WaveOutDevices.List();
        using (var enumerator = new MMDeviceEnumerator())
        {
            var (defaultName, defaultId) = GetDefaultAudioEndpoint(enumerator);
            var deviceList = options.Full ? EnumerateDevices(enumerator) : new List<AudioDeviceInfo>();

            if (options.Json)
            {
                OutputJson(defaultName, defaultId, deviceList, waveOutDevices);
                return 0;
            }

            OutputConsole(defaultName, defaultId, deviceList, options.Full);
            OutputWaveOutDevices(waveOutDevices);
        }

        if (options.Speak is { } voice)
        {
            await SpeakDeviceNumbersAsync(voice, waveOutDevices, cancellationToken);
        }
        return 0;
    }

    /// <summary>
    /// 番号の読み上げ (<c>devices --speak</c>) の文。
    /// </summary>
    public static string DeviceNumberText(int index) => $"デバイス{index}番です。";

    /// <summary>
    /// 先にまとめて合成する文。<see cref="TextSegmentProcessor"/> で分けると、デバイスごとの <see cref="DeviceNumberText"/> と同じ文になる
    /// (同じキャッシュを引けるように)。
    /// </summary>
    public static string AllDeviceNumbersText(IEnumerable<int> devices) => String.Join("\n", devices.Select(DeviceNumberText));

    /// <summary>
    /// WaveOut のデバイスを番号順に、そのデバイスで番号を読み上げる。
    /// </summary>
    /// <remarks>
    /// <para>読み上げと同じ <see cref="TextToSpeechProcessor"/> を通す (ロック・キャッシュ・エンジンの起動もそのまま使う)。</para>
    /// <para>先に全部の文を再生せずに合成してキャッシュし、それからデバイスごとに鳴らす。
    /// デバイスごとに合成すると、<c>KeepEngineRunning: false</c> ではロックを手放すたびにエンジンが止まり、デバイスの数だけ起動し直すため。
    /// デバイスごとの読み上げは <c>--cache-only</c> にしない。その間にほかのプロセスが <c>--clear</c> などでキャッシュを消すことがあり、
    /// そのときは合成し直して続ける。</para>
    /// <para>読み上げの進捗 (<c>Processing segments...</c> など) は出さない (デバイスの一覧に混ざるため)。</para>
    /// <para>失敗 (エンジン・デバイスのエラー) はそこで止める。直前に出した番号と名前で、どのデバイスかが分かる。</para>
    /// </remarks>
    private async Task SpeakDeviceNumbersAsync(VoiceRequest voice, IReadOnlyList<WaveOutDeviceInfo> devices, CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return;
        }

        ConsoleHelper.WriteLine("");
        ConsoleHelper.WriteLine("Speaking each WaveOut index on its device...");
        var prepare = voice.WithText(AllDeviceNumbersText(devices.Select(d => d.Index)));
        await RunOrThrowAsync(prepare, new TextToSpeechOptions(NoCache: false, CacheOnly: false, Verbose: false, OutPath: null, NoPlay: true, Quiet: true), cancellationToken);

        foreach (var device in devices)
        {
            ConsoleHelper.WriteLine($"  {device.Index,2}: \"{device.Name}\"");
            var options = new TextToSpeechOptions(NoCache: false, CacheOnly: false, Verbose: false, OutPath: null, NoPlay: false, Device: device.Index.ToString(CultureInfo.InvariantCulture), Quiet: true);
            await RunOrThrowAsync(voice.WithText(DeviceNumberText(device.Index)), options, cancellationToken);
        }
        _logger.LogInformation("デバイスの番号を読み上げました: {Count} 台", devices.Count);
    }

    /// <summary>
    /// 読み上げを実行し、一部の文の失敗 (終了コード 8) もエラーにする (読み上げの文は 1 文なので、通常は例外になる)。
    /// </summary>
    private async Task RunOrThrowAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken)
    {
        var exitCode = await _ttsProcessor.ProcessTextToSpeechAsync(request, options, cancellationToken);
        if (exitCode != 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Audio.AudioGenerationFailed,
                message: $"Failed to speak device numbers (exit code {exitCode})",
                userMessage: "デバイスの番号を読み上げられませんでした。");
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
        ConsoleHelper.WriteLine("Audio.Devices (WaveOut index):");
        ConsoleHelper.WriteLine("  -1: OS default");
        foreach (var d in waveOutDevices)
        {
            ConsoleHelper.WriteLine($"  {d.Index,2}: \"{d.Name}\"");
        }
    }
}
