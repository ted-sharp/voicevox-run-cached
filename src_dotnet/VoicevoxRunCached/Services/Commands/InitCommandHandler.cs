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

    /// <summary>
    /// フィラーキャッシュの初期化を行います
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleInitAsync()
    {
        try
        {
            if (!await _engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
                return 1;
            }

            ConsoleHelper.WriteLine("Initializing filler cache...", _logger);
            await _fillerManager.InitializeFillerCacheAsync(_apiClient);
            ConsoleHelper.WriteSuccess("Filler cache initialized", _logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error initializing filler cache: {ex.Message}", _logger);
            return 1;
        }
    }
}
