using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 初期化関連のコマンド処理を行うクラス
/// </summary>
public class InitCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly FillerManager _fillerManager;

    public InitCommandHandler(
        VoiceVoxEngineManager engineManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
    }

    public async Task<int> HandleInitAsync(CancellationToken cancellationToken = default)
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

        ConsoleHelper.WriteLine("Initializing filler cache...");
        await _fillerManager.InitializeFillerCacheAsync(_apiClient, cancellationToken);
        ConsoleHelper.WriteSuccess("Filler cache initialized");
        return 0;
    }
}
