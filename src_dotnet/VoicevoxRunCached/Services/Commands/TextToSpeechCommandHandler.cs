using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げ関連のコマンド処理を行うクラス
/// </summary>
public class TextToSpeechCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public TextToSpeechCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// テキストを音声に変換して再生します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="noCache">キャッシュを使用しないフラグ</param>
    /// <param name="cacheOnly">キャッシュのみを使用するフラグ</param>
    /// <param name="verbose">詳細出力フラグ</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="noPlay">再生しないフラグ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleTextToSpeechAsync(VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, CancellationToken cancellationToken = default)
    {
        var totalStartTime = DateTime.UtcNow;
        try
        {
            // VOICEVOXエンジンが動作していることを確認
            var engineStartTime = DateTime.UtcNow;
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (cancellationToken.IsCancellationRequested || !await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Engine check completed in {(DateTime.UtcNow - engineStartTime).TotalMilliseconds:F1}ms", this._logger);
            }

            // 出力ファイルが指定されている場合、バックグラウンドエクスポートタスクを開始（単発の全テキスト生成）
            Task? exportTask = null;
            if (!String.IsNullOrWhiteSpace(outPath))
            {
                exportTask = Task.Run(async () =>
                {
                    try
                    {
                        using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
                        await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

                        var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
                        var wavData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
                        await this.WriteOutputFileAsync(wavData, outPath!, cancellationToken);
                        ConsoleHelper.WriteSuccess($"Saved output to: {outPath}", this._logger);
                    }
                    catch (OperationCanceledException)
                    {
                        ConsoleHelper.WriteLine("Output export was cancelled", this._logger);
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}", this._logger);
                    }
                }, cancellationToken);
            }

            if (noPlay)
            {
                if (exportTask != null)
                {
                    await exportTask;
                }
                ConsoleHelper.WriteSuccess("Done (no-play mode)!", this._logger);
                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
                return 0;
            }

            var cacheManager = new AudioCacheManager(this._settings.Cache);
            byte[]? audioData = null;

            // キャッシュ効率を向上させるため、テキストをセグメントで処理
            if (!noCache)
            {
                var segmentStartTime = DateTime.UtcNow;
                ConsoleHelper.WriteLine("Processing segments...", this._logger);
                cancellationToken.ThrowIfCancellationRequested();

                // テキストセグメントを分割
                var textProcessor = new TextSegmentProcessor();
                var segments = await textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);

                // セグメントの位置情報を更新
                textProcessor.UpdateSegmentPositions(segments);

                // キャッシュからセグメントを処理
                var processedSegments = await cacheManager.ProcessTextSegmentsAsync(segments, request, cancellationToken);

                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Segment processing completed in {(DateTime.UtcNow - segmentStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
                var cachedCount = processedSegments.Count(s => s.IsCached);
                var totalCount = processedSegments.Count;

                if (cachedCount > 0)
                {
                    ConsoleHelper.WriteSuccess($"Found {cachedCount}/{totalCount} segments in cache!", this._logger);
                }

                var uncachedSegments = processedSegments.Where(s => !s.IsCached).ToList();

                // 未キャッシュセグメントのバックグラウンド生成を開始
                AudioProcessingChannel? processingChannel = null;
                if (uncachedSegments.Count > 0)
                {
                    if (cacheOnly)
                    {
                        ConsoleHelper.WriteError($"{uncachedSegments.Count} segments not cached and --cache-only specified", this._logger);
                        return 1;
                    }

                    ConsoleHelper.WriteWarning($"Generating {uncachedSegments.Count} segments using processing channel...", this._logger);
                    processingChannel = new AudioProcessingChannel(cacheManager, new VoiceVoxApiClient(this._settings.VoiceVox));

                    // 未キャッシュセグメントの生成を並行で開始
                    var generationTasks = new List<Task>();
                    foreach (var segment in uncachedSegments)
                    {
                        var generationTask = Task.Run(async () =>
                        {
                            try
                            {
                                var segmentRequest = new VoiceRequest
                                {
                                    Text = segment.Text,
                                    SpeakerId = segment.SpeakerId ?? request.SpeakerId,
                                    Speed = request.Speed,
                                    Pitch = request.Pitch,
                                    Volume = request.Volume
                                };

                                var result = await processingChannel.ProcessAudioAsync(segmentRequest, cancellationToken);
                                if (result.Success && result.AudioData.Length > 0)
                                {
                                    segment.AudioData = result.AudioData;
                                    segment.IsCached = true;
                                    if (verbose)
                                    {
                                        this._logger.LogInformation("セグメントの並行生成が完了: \"{Text}\" (サイズ: {Size} bytes)",
                                            segment.Text, result.AudioData.Length);
                                    }
                                }
                                else
                                {
                                    this._logger.LogWarning("セグメントの並行生成に失敗: \"{Text}\" - {Error}",
                                        segment.Text, result.ErrorMessage ?? "Unknown error");
                                }
                            }
                            catch (Exception ex)
                            {
                                this._logger.LogError(ex, "セグメントの並行生成中にエラーが発生: \"{Text}\"", segment.Text);
                            }
                        }, cancellationToken);

                        generationTasks.Add(generationTask);
                    }

                    // 生成タスクの完了を少し待機（一部のセグメントが準備完了するまで）
                    if (generationTasks.Count > 0)
                    {
                        var shortDelay = Math.Min(2000, 1000 + (generationTasks.Count * 100)); // セグメント数に応じて待機時間を調整
                        ConsoleHelper.WriteLine($"Waiting for initial segment generation ({shortDelay}ms)...", this._logger);
                        await Task.Delay(shortDelay, cancellationToken);

                        // 完了したタスクを確認
                        var completedTasks = generationTasks.Where(t => t.IsCompleted).ToList();
                        if (completedTasks.Count > 0)
                        {
                            ConsoleHelper.WriteSuccess($"{completedTasks.Count}/{generationTasks.Count} segments generated in background!", this._logger);
                        }

                        // 残りのタスクの完了を待機（最大30秒）
                        var remainingTasks = generationTasks.Where(t => !t.IsCompleted).ToList();
                        if (remainingTasks.Count > 0)
                        {
                            ConsoleHelper.WriteLine($"Waiting for remaining {remainingTasks.Count} segments to complete...", this._logger);
                            try
                            {
                                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                                await Task.WhenAll(remainingTasks).WaitAsync(timeoutCts.Token);
                                ConsoleHelper.WriteSuccess("All remaining segments completed!", this._logger);
                            }
                            catch (OperationCanceledException)
                            {
                                ConsoleHelper.WriteWarning("Timeout waiting for remaining segments, continuing with available ones", this._logger);
                            }
                            catch (Exception ex)
                            {
                                ConsoleHelper.WriteWarning($"Error waiting for remaining segments: {ex.Message}", this._logger);
                            }
                        }

                        // 最終的な完了状況を確認
                        var finalCompletedTasks = generationTasks.Where(t => t.IsCompleted).ToList();
                        ConsoleHelper.WriteSuccess($"Final status: {finalCompletedTasks.Count}/{generationTasks.Count} segments ready for playback", this._logger);
                    }
                }

                // 即座に再生開始 - キャッシュ済みセグメントは即座に再生、未キャッシュセグメントは待機
                var playbackStartTime = DateTime.UtcNow;
                ConsoleHelper.WriteInfo("Playing audio...", this._logger);
                using var audioPlayer = new AudioPlayer(this._settings.Audio);
                FillerManager? fillerManager = null;
                if (this._settings.Filler.Enabled)
                {
                    fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);
                    // 使用前にフィラーキャッシュを初期化
                    await fillerManager.InitializeFillerCacheAsync(this._settings);
                }
                await audioPlayer.PlayAudioSequentiallyWithGenerationAsync(processedSegments, processingChannel, fillerManager, cancellationToken);

                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Audio playback completed in {(DateTime.UtcNow - playbackStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
            }
            else
            {
                // --no-cacheの場合は元の非キャッシュ動作
                using var spinner = new ProgressSpinner("Generating speech...");
                using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);

                await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

                var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
                audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);

                ConsoleHelper.WriteInfo("Playing audio...", this._logger);
                using var audioPlayer = new AudioPlayer(this._settings.Audio);
                await audioPlayer.PlayAudioAsync(audioData, cancellationToken);
            }

            ConsoleHelper.WriteSuccess("Done!", this._logger);

            if (exportTask != null)
            {
                await exportTask;
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }

    /// <summary>
    /// 音声データをファイルに出力します
    /// </summary>
    /// <param name="wavData">WAV音声データ</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>出力完了を表すTask</returns>
    private async Task WriteOutputFileAsync(byte[] wavData, string outPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(outPath).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        if (extension == ".mp3")
        {
            try
            {
                var mp3Bytes = AudioConversionUtility.ConvertWavToMp3(wavData);
                // MP3ヘッダーの健全性チェック（0xFFEx）
                bool isMp3 = mp3Bytes.Length >= 2 && mp3Bytes[0] == 0xFF && (mp3Bytes[1] & 0xE0) == 0xE0;
                if (!isMp3)
                {
                    // フォールバック: 拡張子を修正してWAVとして書き込み
                    var wavOut = Path.ChangeExtension(outPath, ".wav");
                    await File.WriteAllBytesAsync(wavOut, wavData);
                    ConsoleHelper.WriteWarning($"MP3 encoding fallback to WAV: {wavOut}", this._logger);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await File.WriteAllBytesAsync(outPath, mp3Bytes);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to export MP3: {ex.Message}", ex);
            }
            return;
        }

        // デフォルト: WAVバイトをそのまま書き込み
        // 拡張子が一致しない場合は警告して修正
        bool isWav = wavData.Length >= 12 &&
                     wavData[0] == 'R' && wavData[1] == 'I' && wavData[2] == 'F' && wavData[3] == 'F' &&
                     wavData[8] == 'W' && wavData[9] == 'A' && wavData[10] == 'V' && wavData[11] == 'E';
        if (!isWav)
        {
            // 予期しないが、要求されたパスに生バイトを書き込み
            await File.WriteAllBytesAsync(outPath, wavData);
            return;
        }

        if (extension != ".wav")
        {
            var corrected = Path.ChangeExtension(outPath, ".wav");
            await File.WriteAllBytesAsync(corrected, wavData);
            ConsoleHelper.WriteWarning($"Adjusted extension to .wav: {corrected}", this._logger);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(outPath, wavData);
        }
    }
}
