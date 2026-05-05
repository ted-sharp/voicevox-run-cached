using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// スピーカー関連のコマンド処理を行うクラス
/// </summary>
public class SpeakerCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;

    public SpeakerCommandHandler(VoiceVoxEngineManager engineManager, VoiceVoxApiClient apiClient)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public async Task<int> HandleSpeakersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEngineOrThrowAsync(cancellationToken);

        var speakers = await _apiClient.GetSpeakersAsync(cancellationToken);

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

    private async Task EnsureEngineOrThrowAsync(CancellationToken cancellationToken)
    {
        if (!await _engineManager.EnsureEngineRunningAsync(cancellationToken))
        {
            throw new VoicevoxRunCachedException(
                ErrorCodes.Engine.EngineNotAvailable,
                "VOICEVOX engine is not available",
                "VOICEVOXエンジンが利用できません。",
                null,
                "VOICEVOXエンジンが起動しているか確認してください。");
        }
    }
}
