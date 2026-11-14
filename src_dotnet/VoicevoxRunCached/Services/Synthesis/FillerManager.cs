using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// フィラー音声 (合成待ちの隙間に挟む相槌) の生成と取得。
/// </summary>
/// <remarks>
/// フィラーは読み上げと同じ話者の声で再生する (途中で声が変わらないように)。キャッシュキーにエンジンと話者が含まれるので、
/// 話者ごとに <c>--init [--engine &lt;名前&gt;] -s &lt;話者&gt;</c> で生成しておく。生成していない話者ではフィラーを鳴らさない。
/// 文はエンジンごとに変えられる (エンジンのセクションの <c>FillerTexts</c>。空なら <see cref="FillerSettings.FillerTexts"/>)。
/// 合成に時間のかかるエンジン (VOICEPEAK) では長めの文にして、待ちを少ない回数で埋める。
/// </remarks>
public class FillerManager
{
    private readonly FillerSettings _settings;
    private readonly int _opusBitrateKbps;
    private readonly ILogger _logger;
    private string? _lastUsedFiller;
    private bool _missingFillerWarned;

    public FillerManager(FillerSettings settings, CacheSettings cacheSettings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(cacheSettings);
        _opusBitrateKbps = cacheSettings.OpusBitrateKbps;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogDebug("FillerManager を初期化しました - 有効: {Enabled}, ディレクトリ: {Directory}",
            _settings.Enabled, _settings.Directory);
    }

    /// <param name="engine">合成に使うエンジン。準備 (<see cref="ISpeechEngine.PrepareAsync"/>) は呼び出し元で済ませておく</param>
    /// <param name="speaker">フィラーを生成する話者</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>生成を試みた件数と成否。生成済みでスキップしたフィラーは数えない</returns>
    /// <remarks>
    /// エンジンに届かない失敗 (<see cref="ISpeechEngine.IsEngineUnavailable"/>) が起きたら、残りの未生成分は
    /// エンジンを呼ばずに失敗として数える (<c>--init</c> は読み上げと同じロックを持つので、1 件ごとにタイムアウト
    /// (ConnectionTimeout) を待つと、その間 Hooks の読み上げも待たせてしまう)。
    /// </remarks>
    public async Task<SynthesisResult> InitializeFillerCacheAsync(ISpeechEngine engine, string speaker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Filler.Enabled は再生にだけ効く。生成してから有効にする手順 (appsettings.json のコメント・README) なので、
        // 無効のままでも生成する
        Directory.CreateDirectory(_settings.Directory);
        DeleteLegacyFiles();
        var fillerTexts = FillerTextsFor(engine);
        _logger.LogInformation("フィラーキャッシュの初期化を開始します - テキスト数: {Count}", fillerTexts.Length);

        using var spinner = new ProgressSpinner("Initializing filler cache...");

        int processed = 0;
        int attempted = 0;
        int failed = 0;
        int total = fillerTexts.Length;
        var engineUnavailable = false;
        int aborted = 0;
        Exception? firstError = null;

        foreach (var fillerText in fillerTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            spinner.UpdateMessage($"Generating filler {processed}/{total}: \"{fillerText}\"");
            _logger.LogDebug("フィラー音声を生成中 {Processed}/{Total}: \"{Text}\"", processed, total, fillerText);

            var fillerRequest = BuildFillerRequest(fillerText, engine.Kind, speaker);
            var fillerCachePath = ResolveFillerCachePath(fillerRequest);

            if (File.Exists(fillerCachePath))
            {
                _logger.LogDebug("フィラー音声は既にキャッシュ済み: \"{Text}\"", fillerText);
                continue;
            }

            attempted++;
            if (engineUnavailable)
            {
                // 生成を中止した後の未生成分
                failed++;
                aborted++;
                continue;
            }

            try
            {
                var audioData = await engine.SynthesizeAsync(fillerRequest, cancellationToken);

                // 一時ファイルに書いてから置き換える。書き込みの途中で落ちて不完全な .opus が残ると、--init は生成済みとみなして
                // 作り直さず、再生のたびにデコードに失敗してフィラーが使われなくなるため。
                // 残った .tmp は同じフィラーを次に生成するときに上書きされ、--clear (ClearFillerCacheAsync) でも消える
                var tempPath = fillerCachePath + DiskCacheStore.TemporarySuffix;
                await File.WriteAllBytesAsync(tempPath, OpusCodec.EncodeWav(audioData, _opusBitrateKbps), cancellationToken);
                File.Move(tempPath, fillerCachePath, overwrite: true);
                _logger.LogDebug("フィラー音声を生成・保存しました: \"{Text}\"", fillerText);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "フィラー音声の生成に失敗: \"{Text}\"", fillerText);

                firstError ??= ex;
                engineUnavailable = engine.IsEngineUnavailable(ex);
            }
        }

        // 残りが生成済みだけなら打ち切ったものは無いので、実際に API を呼ばなかった件数で出す
        if (aborted > 0)
        {
            _logger.LogWarning("合成エンジンを使えない (接続できない・応答しない等) ため、{Count} 件のフィラーの生成を中止しました", aborted);
        }

        _logger.LogInformation("フィラーキャッシュの初期化が完了しました - 生成: {Success}, 失敗: {Failure}, 生成済み: {Skipped}",
            attempted - failed, failed, total - attempted);
        return new SynthesisResult(attempted, attempted - failed, failed, FirstError: firstError);
    }

    /// <summary>
    /// v2 以前の MP3 フィラーを削除する。もう読まれないファイルなので、削除できなくてもフィラーの生成は続ける
    /// (本体キャッシュの <see cref="DiskCacheStore.DeleteLegacyFiles"/> と同じく best-effort)。
    /// </summary>
    private void DeleteLegacyFiles()
    {
        foreach (var file in Directory.GetFiles(_settings.Directory, $"*{DiskCacheStore.LegacyAudioExtension}"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "旧形式フィラーの削除に失敗: {Path}", file);
            }
        }
    }

    /// <param name="engine">読み上げ中のエンジン。そのエンジンのフィラーの文 (<see cref="ISpeechEngine.FillerSettings"/>) から選ぶ</param>
    /// <param name="speaker">読み上げ中の話者。そのエンジン・話者のフィラーだけを返す</param>
    /// <param name="volume">読み上げの音量 (<c>--volume</c>)。フィラーの音量もこれに合わせる</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <returns>フィラーの音声。音量が 1.0 なら生成したときの Ogg Opus、それ以外は音量を掛けた WAV。無効・未生成なら null</returns>
    /// <remarks>
    /// フィラーは既定のパラメータ (話速 1.0・音高 0.0・音量 1.0) で生成してあり、話速・音高は読み上げと違うことがある。
    /// 音量は再生時に掛けて合わせる (VOICEVOX の volumeScale も振幅に掛ける線形の倍率なので、効き方が同じになる)。
    /// </remarks>
    public async Task<byte[]?> GetRandomFillerAudioAsync(ISpeechEngine engine, string speaker, double volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var fillerTexts = FillerTextsFor(engine);
        if (!_settings.Enabled || fillerTexts.Length == 0)
        {
            _logger.LogDebug("フィラー機能が無効またはテキストが設定されていません");
            return null;
        }

        string selectedFiller = SelectFillerText(fillerTexts);
        _lastUsedFiller = selectedFiller;
        _logger.LogDebug("ランダムフィラー音声を選択: \"{Text}\"", selectedFiller);

        var fillerRequest = BuildFillerRequest(selectedFiller, engine.Kind, speaker);
        var fillerCachePath = ResolveFillerCachePath(fillerRequest);

        if (!File.Exists(fillerCachePath))
        {
            // 合成待ちのたびに (800ms ごとに) 呼ばれるので、Warning は 1 実行につき 1 回にする
            if (!_missingFillerWarned)
            {
                // 一部だけ未生成のこともある (FillerTexts を追加した・--init が一部失敗した)。--init は生成済みを飛ばすので案内は同じ
                _logger.LogWarning("この話者に未生成のフィラーがあります (--init --engine {Engine} -s {Speaker} で生成できます) - text=\"{Text}\"",
                    engine.Kind.ToString().ToLowerInvariant(), speaker, selectedFiller);
                _missingFillerWarned = true;
            }
            else
            {
                _logger.LogDebug("話者 {Speaker} ({Engine}) のフィラーが未生成です - text=\"{Text}\"", speaker, engine.Kind, selectedFiller);
            }
            return null;
        }

        var data = await File.ReadAllBytesAsync(fillerCachePath, cancellationToken);
        _logger.LogDebug("フィラーキャッシュファイルを読み込みました: {Path} (サイズ: {Size} bytes)",
            Path.GetFileName(fillerCachePath), data.Length);

        var gain = CacheKeyGenerator.NormalizeParameter(volume);
        return gain == 1.0 ? data : AudioConversionUtility.ApplyGain(OpusCodec.DecodeToWav(data), (float)gain);
    }

    /// <summary>
    /// エンジンのフィラーの文 (エンジンのセクションの <c>FillerTexts</c>)。空なら共通の <see cref="FillerSettings.FillerTexts"/>。
    /// </summary>
    private string[] FillerTextsFor(ISpeechEngine engine)
        => engine.FillerSettings.FillerTexts.Length > 0 ? engine.FillerSettings.FillerTexts : _settings.FillerTexts;

    private string SelectFillerText(string[] fillerTexts)
    {
        if (fillerTexts.Length == 1)
        {
            return fillerTexts[0];
        }

        var availableFillers = fillerTexts
            .Where(f => f != _lastUsedFiller)
            .ToArray();

        if (availableFillers.Length == 0)
        {
            availableFillers = fillerTexts;
        }

        return availableFillers[Random.Shared.Next(availableFillers.Length)];
    }

    private static VoiceRequest BuildFillerRequest(string text, SpeechEngineKind engine, string speaker) => new()
    {
        Text = text,
        Engine = engine,
        Speaker = speaker,
        Speed = 1.0,
        Pitch = 0.0,
        Volume = 1.0
    };

    private string ResolveFillerCachePath(VoiceRequest fillerRequest)
    {
        var cacheKey = CacheKeyGenerator.Compute(fillerRequest);
        return Path.Combine(_settings.Directory, $"{cacheKey}{DiskCacheStore.AudioExtension}");
    }

    /// <summary>
    /// フィラーキャッシュを削除する (旧形式の MP3 と、生成の途中で落ちて残った一時ファイルも含む)。
    /// 失敗は CacheException として呼び出し元に伝える。
    /// </summary>
    public Task ClearFillerCacheAsync()
    {
        string[] searchPatterns =
        [
            $"*{DiskCacheStore.AudioExtension}",
            $"*{DiskCacheStore.LegacyAudioExtension}",
            $"*{DiskCacheStore.AudioExtension}{DiskCacheStore.TemporarySuffix}",
        ];
        CacheErrorMapper.WithMapping("フィラーキャッシュのクリア", null, _settings.Directory, _logger, () =>
        {
            if (!Directory.Exists(_settings.Directory))
                return;

            foreach (var file in searchPatterns.SelectMany(p => Directory.GetFiles(_settings.Directory, p)))
            {
                File.Delete(file);
            }
        });
        return Task.CompletedTask;
    }
}
