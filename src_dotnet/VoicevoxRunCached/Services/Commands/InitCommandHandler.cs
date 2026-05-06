using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 初期化関連のコマンド処理を行うクラス
/// </summary>
public class InitCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly FillerManager _fillerManager;
    private readonly ILogger _logger;

    public InitCommandHandler(
        VoiceVoxEngineManager engineManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager,
        ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> HandleInitAsync(CancellationToken cancellationToken = default)
    {
        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        _logger.LogInformation("フィラーキャッシュの初期化を開始します");
        ConsoleHelper.WriteLine("Initializing filler cache...");
        await _fillerManager.InitializeFillerCacheAsync(_apiClient, cancellationToken);
        ConsoleHelper.WriteSuccess("Filler cache initialized");
        return 0;
    }
}
