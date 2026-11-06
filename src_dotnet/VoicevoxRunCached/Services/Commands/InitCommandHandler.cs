using Microsoft.Extensions.Logging;
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
        var result = await _fillerManager.InitializeFillerCacheAsync(_apiClient, cancellationToken);

        // 終了コードは通常の読み上げと揃える: 全部失敗は AUDIO エラー (5)、一部失敗は PartialFailureExitCode (8)
        if (result.AttemptedCount > 0 && result.SuccessCount == 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Audio.AudioGenerationFailed,
                message: $"All {result.AttemptedCount} fillers failed to generate",
                userMessage: "全てのフィラー音声の生成に失敗しました。",
                suggestedSolution: "VOICEVOXエンジンの状態とログを確認してください。");
        }

        if (result.FailureCount > 0)
        {
            _logger.LogWarning("フィラー音声の生成が部分的に失敗しました: {FailureCount}/{AttemptedCount}",
                result.FailureCount, result.AttemptedCount);
            ConsoleHelper.WriteWarning($"Warning: {result.FailureCount}/{result.AttemptedCount} fillers failed to generate");
            return ErrorCodes.PartialFailureExitCode;
        }

        ConsoleHelper.WriteSuccess("Filler cache initialized");
        return 0;
    }
}
