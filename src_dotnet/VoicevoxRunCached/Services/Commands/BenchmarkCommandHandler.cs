using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// ベンチマーク関連のコマンド処理を行うクラス
/// </summary>
public class BenchmarkCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public BenchmarkCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// パフォーマンスベンチマークを実行します
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleBenchmarkAsync()
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

            ConsoleHelper.WriteLine("Starting performance benchmark...", this._logger);

            // ウォームアップ
            ConsoleHelper.WriteLine("Warming up...", this._logger);

            using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
            await apiClient.InitializeSpeakerAsync(this._settings.VoiceVox.DefaultSpeaker);

            // ベンチマーク
            ConsoleHelper.WriteLine("Benchmarking...", this._logger);
            var segments = new List<TextSegment>
            {
                new TextSegment { Text = "Hello, this is a performance benchmark." },
                new TextSegment { Text = "This is a longer text to test the caching mechanism." },
                new TextSegment { Text = "And another segment to ensure the pipeline is efficient." }
            };

            using var spinner = new ProgressSpinner("Benchmarking...");
            var totalStartTime = DateTime.UtcNow;

            for (int i = 0; i < 10; i++) // ベンチマークを10回実行
            {
                spinner.UpdateMessage($"Benchmark iteration {i + 1}/10");
                var request = new VoiceRequest
                {
                    Text = segments[i % segments.Count].Text, // セグメントを循環
                    SpeakerId = this._settings.VoiceVox.DefaultSpeaker,
                    Speed = 1.0,
                    Pitch = 0.0,
                    Volume = 1.0
                };

                var audioQuery = await apiClient.GenerateAudioQueryAsync(request, CancellationToken.None);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, CancellationToken.None);
            }

            var elapsedTime = (DateTime.UtcNow - totalStartTime).TotalMilliseconds;
            ConsoleHelper.WriteSuccess($"Benchmark completed. Total time: {elapsedTime:F1}ms", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error during benchmark: {ex.Message}", this._logger);
            return 1;
        }
    }
}
