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

    public async Task<int> HandleSpeakersAsync(CancellationToken cancellationToken = default)
    {
        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        var speakers = await _apiClient.GetSpeakersAsync(cancellationToken);
        _logger.LogInformation("スピーカー一覧を取得しました - 件数: {Count}", speakers.Count);

        ConsoleHelper.WriteLine("Available speakers:");
        foreach (var speaker in speakers)
        {
            ConsoleHelper.WriteLine($"  {speaker.Name} (v{speaker.Version})");
            foreach (var style in speaker.Styles)
            {
                ConsoleHelper.WriteLine($"    ID: {style.Id} - {style.Name}");
            }
            ConsoleHelper.WriteLine("");
        }
        return 0;
    }
}
