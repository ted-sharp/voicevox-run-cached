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

    /// <param name="speakerId">フィラーを生成する話者。フィラーは読み上げと同じ話者の声で再生されるので、話者ごとに生成する</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <remarks>
    /// 読み上げと同じ <see cref="InterProcessLock.SpeechLockPath"/> で直列化する。読み上げ中に実行しても
    /// API 呼び出しが並列にならず (VOICEVOX は直列前提)、エンジンの自動起動も二重にならない。
    /// </remarks>
    public async Task<int> HandleInitAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        using var speechLock = await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, _logger, cancellationToken);

        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        // 存在しない話者だと /initialize_speaker が 500 (原因の分からないエンジンエラー) になり、リトライの分だけ待たされるので、先に確認して入力ミスとして即エラーにする
        TextToSpeechProcessor.EnsureSpeakerExists(await _apiClient.GetSpeakersAsync(cancellationToken), speakerId);

        _logger.LogInformation("フィラーキャッシュの初期化を開始します - 話者: {SpeakerId}", speakerId);
        ConsoleHelper.WriteLine($"Initializing filler cache (speaker {speakerId})...");
        var result = await _fillerManager.InitializeFillerCacheAsync(_apiClient, speakerId, cancellationToken);

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
