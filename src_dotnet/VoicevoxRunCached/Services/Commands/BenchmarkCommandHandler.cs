using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// ベンチマーク関連のコマンド処理を行うクラス
/// </summary>
public class BenchmarkCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly ILogger _logger;
    private readonly VoiceVoxSettings _settings;

    public BenchmarkCommandHandler(
        VoiceVoxEngineManager engineManager,
        VoiceVoxApiClient apiClient,
        VoiceVoxSettings settings,
        ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// パフォーマンスベンチマークを実行します
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleBenchmarkAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _engineManager.EnsureEngineRunningAsync(cancellationToken))
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
                return 1;
            }

            ConsoleHelper.WriteLine("Starting performance benchmark...", _logger);
            ConsoleHelper.WriteLine("Warming up...", _logger);

            await _apiClient.InitializeSpeakerAsync(_settings.DefaultSpeaker, cancellationToken);

            ConsoleHelper.WriteLine("Benchmarking...", _logger);
            var segments = new List<TextSegment>
            {
                new TextSegment { Text = "こんにちは、これはパフォーマンス計測です。" },
                new TextSegment { Text = "キャッシュ機構を検証するための、少し長めのテキストです。" },
                new TextSegment { Text = "もう一つのセグメントでパイプラインの効率を確認します。" }
            };

            using var spinner = new ProgressSpinner("Benchmarking...");
            var totalStartTime = DateTime.UtcNow;

            for (int i = 0; i < 10; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                spinner.UpdateMessage($"Benchmark iteration {i + 1}/10");
                var request = new VoiceRequest
                {
                    Text = segments[i % segments.Count].Text,
                    SpeakerId = _settings.DefaultSpeaker,
                    Speed = 1.0,
                    Pitch = 0.0,
                    Volume = 1.0
                };

                var audioQuery = await _apiClient.GenerateAudioQueryAsync(request, cancellationToken);
                _ = await _apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
            }

            var elapsedTime = (DateTime.UtcNow - totalStartTime).TotalMilliseconds;
            ConsoleHelper.WriteSuccess($"Benchmark completed. Total time: {elapsedTime:F1}ms", _logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error during benchmark: {ex.Message}", _logger);
            return 1;
        }
    }
}
