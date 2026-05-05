using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// スピーカー関連のコマンド処理を行うクラス
/// </summary>
public class SpeakerCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly ILogger _logger;

    public SpeakerCommandHandler(VoiceVoxEngineManager engineManager, VoiceVoxApiClient apiClient, ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 利用可能なスピーカーの一覧を取得・表示します
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleSpeakersAsync()
    {
        try
        {
            if (!await _engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
                return 1;
            }

            var speakers = await _apiClient.GetSpeakersAsync();

            ConsoleHelper.WriteLine("Available speakers:", _logger);
            foreach (var speaker in speakers)
            {
                ConsoleHelper.WriteLine($"  {speaker.Name} (v{speaker.Version})", _logger);
                foreach (var style in speaker.Styles)
                {
                    ConsoleHelper.WriteLine($"    ID: {style.Id} - {style.Name}", _logger);
                }
                Console.WriteLine();
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error: {ex.Message}", _logger);
            return 1;
        }
    }
}
