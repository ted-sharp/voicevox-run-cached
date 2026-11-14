using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 初期化関連のコマンド処理を行うクラス
/// </summary>
public class InitCommandHandler
{
    private readonly SpeechEngineProvider _engines;
    private readonly FillerManager _fillerManager;
    private readonly SpeechLock _speechLock;
    private readonly ILogger _logger;

    public InitCommandHandler(
        SpeechEngineProvider engines,
        FillerManager fillerManager,
        SpeechLock speechLock,
        ILogger logger)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _speechLock = speechLock ?? throw new ArgumentNullException(nameof(speechLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="engine">フィラーを生成するエンジン</param>
    /// <param name="speaker">フィラーを生成する話者。フィラーは読み上げと同じ話者の声で再生されるので、話者ごとに生成する</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <remarks>
    /// 読み上げと同じ <see cref="SpeechLock"/> で直列化する。読み上げ中に実行しても
    /// エンジンの呼び出しが並列にならず、エンジンの自動起動も二重にならない。
    /// </remarks>
    public Task<int> HandleInitAsync(SpeechEngineKind engine, string speaker, CancellationToken cancellationToken = default)
        => _speechLock.RunAsync(() => InitializeLockedAsync(_engines.Get(engine), speaker, cancellationToken), cancellationToken);

    private async Task<int> InitializeLockedAsync(ISpeechEngine engine, string speaker, CancellationToken cancellationToken)
    {
        // 起動確認・話者の存在確認 (存在しなければ入力ミスとして即エラー)・話者の初期化
        await engine.PrepareAsync(speaker, cancellationToken);

        _logger.LogInformation("フィラーキャッシュの初期化を開始します - エンジン: {Engine}, 話者: {Speaker}", engine.Kind, speaker);
        ConsoleHelper.WriteLine($"Initializing filler cache ({engine.Kind.ToString().ToLowerInvariant()} speaker {speaker})...");
        var result = await _fillerManager.InitializeFillerCacheAsync(engine, speaker, cancellationToken);

        // 終了コードは通常の読み上げと揃える: 全部失敗は AUDIO エラー (5)、一部失敗は PartialFailureExitCode (8)
        if (result.AttemptedCount > 0 && result.SuccessCount == 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Audio.AudioGenerationFailed,
                message: $"All {result.AttemptedCount} fillers failed to generate",
                userMessage: "全てのフィラー音声の生成に失敗しました。",
                suggestedSolution: "合成エンジンの状態とログを確認してください。");
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
