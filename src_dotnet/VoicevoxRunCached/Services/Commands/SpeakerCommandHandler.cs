using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// スピーカー関連のコマンド処理を行うクラス
/// </summary>
public class SpeakerCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public SpeakerCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 利用可能なスピーカーの一覧を取得・表示します
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleSpeakersAsync()
    {
        try
        {
            // VOICEVOXエンジンが動作していることを確認
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
            var speakers = await apiClient.GetSpeakersAsync();

            ConsoleHelper.WriteLine("Available speakers:", this._logger);
            foreach (var speaker in speakers)
            {
                ConsoleHelper.WriteLine($"  {speaker.Name} (v{speaker.Version})", this._logger);
                foreach (var style in speaker.Styles)
                {
                    ConsoleHelper.WriteLine($"    ID: {style.Id} - {style.Name}", this._logger);
                }
                Console.WriteLine();
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }
}
