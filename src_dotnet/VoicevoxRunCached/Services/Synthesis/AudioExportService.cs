using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// 音声ファイルの出力処理を行うクラス
/// </summary>
public class AudioExportService
{
    /// <summary><c>--out</c> で指定できる拡張子 (小文字)。</summary>
    public static readonly string[] SupportedExtensions = [".wav", ".opus"];

    /// <summary>
    /// 結合後のフォーマット。キャッシュ由来 (Opus デコード = 48kHz) と今回合成分 (VOICEVOX = 24kHz) が混在するため、
    /// 情報を落とさない 48kHz mono 16bit に揃える。
    /// </summary>
    private static readonly WaveFormat OutputFormat = new(AudioConstants.OpusDecodeSampleRate, 16, 1);

    private readonly AudioFormatDetector _formatDetector;
    private readonly int _opusBitrateKbps;
    private readonly ILogger _logger;

    public AudioExportService(AudioFormatDetector formatDetector, CacheSettings cacheSettings, ILogger logger)
    {
        _formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
        ArgumentNullException.ThrowIfNull(cacheSettings);
        _opusBitrateKbps = cacheSettings.OpusBitrateKbps;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExportSegmentsAsync(List<TextSegment> segments, string outPath, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("セグメント音声ファイルの出力を開始します: {OutPath}, セグメント数: {Count}", outPath, segments.Count);

            var validSegments = segments.Where(s => s.AudioData != null && s.AudioData.Length > 0).ToList();
            if (validSegments.Count == 0)
            {
                throw new AudioProcessingException(
                    ErrorCodes.Audio.AudioFileExportFailed,
                    "No valid segments to export",
                    "出力可能なセグメントがありません。",
                    "事前に合成を実行し、AudioData が埋まったセグメントを渡してください。");
            }

            var combinedWavData = CombineWavSegments(validSegments.Select(s => s.AudioData!).ToList(), cancellationToken);

            var extension = Path.GetExtension(outPath).ToLowerInvariant();
            byte[] finalAudioData = extension == ".opus"
                ? OpusCodec.EncodeWav(combinedWavData, _opusBitrateKbps)
                : combinedWavData;

            await WriteOutputFileAsync(finalAudioData, outPath, cancellationToken);

            ConsoleHelper.WriteSuccess($"Saved output to: {outPath}");
            _logger.LogInformation("セグメント音声ファイルの出力が完了しました: {OutPath}", outPath);
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteWarning("Output export was cancelled");
            throw;
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "セグメント音声ファイルの出力に失敗しました: {OutPath}", outPath);
            ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}");
            throw new AudioProcessingException(
                ErrorCodes.Audio.AudioFileExportFailed,
                $"Failed to export combined segments: {outPath}",
                $"音声ファイルの出力に失敗しました: {outPath}",
                ex,
                "出力先ディレクトリの権限と空き容量を確認してください。");
        }
    }

    /// <summary>
    /// 各セグメントを PCM にデコードして 1 本の WAV に結合する。
    /// </summary>
    /// <remarks>
    /// キャッシュヒットしたセグメントは Opus をデコードした 48kHz の WAV、今回合成したセグメントは VOICEVOX の 24kHz の WAV なので、
    /// <see cref="AudioFormatDetector.CreateWaveStream"/> で読み、<see cref="OutputFormat"/> に揃えて結合する。
    /// 1 件のみの場合も同じ経路を通す (出力の形式を常に <see cref="OutputFormat"/> にするため)。
    /// </remarks>
    private byte[] CombineWavSegments(List<byte[]> audioDataList, CancellationToken cancellationToken)
    {
        using var outputStream = new MemoryStream();
        using (var writer = new WaveFileWriter(outputStream, OutputFormat))
        {
            foreach (var audioData in audioDataList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var reader = _formatDetector.CreateWaveStream(audioData);
                var provider = ToOutputFormat(reader.ToSampleProvider());

                var buffer = new float[OutputFormat.SampleRate];
                int read;
                while ((read = provider.Read(buffer)) > 0)
                {
                    writer.WriteSamples(buffer, 0, read);
                }
            }
        }
        return outputStream.ToArray();
    }

    /// <summary>
    /// mono・<see cref="OutputFormat"/> のサンプルレートに揃える。
    /// </summary>
    private static ISampleProvider ToOutputFormat(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source);
        }
        if (source.WaveFormat.SampleRate != OutputFormat.SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, OutputFormat.SampleRate);
        }
        return source;
    }

    /// <summary>
    /// <c>--out</c> の出力先に書き込めるかを確かめる (フォルダが無ければ作る)。書き込めなければ <see cref="ErrorCodes.Audio.AudioFileExportFailed"/>。
    /// </summary>
    /// <remarks>
    /// 出力は合成・再生がすべて終わってからなので、ここで確かめないと、書き込めないことに読み上げを最後まで聞いてから気づく。
    /// 呼び出し元 (<see cref="TextToSpeechProcessor"/>) はプロセス間ロックを待つ前に呼ぶ。
    /// </remarks>
    public static void EnsureWritableOutput(string outPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);

        var fullPath = Path.GetFullPath(outPath);
        if (Directory.Exists(fullPath))
        {
            throw NotWritable(outPath, $"出力先がフォルダです: {outPath}", "--out にはファイル名 (.wav / .opus) を指定してください。");
        }
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null || !DirectoryValidation.BeWritableDirectory(directory))
        {
            throw NotWritable(outPath, $"出力先のフォルダに書き込めません: {outPath}", "出力先のフォルダの場所と権限を確認してください。");
        }
        if (File.Exists(fullPath) && !CanReplace(fullPath))
        {
            throw NotWritable(outPath, $"出力先のファイルを上書きできません: {outPath}", "ファイルが読み取り専用でないか、ほかのアプリで開いていないか確認してください。");
        }
    }

    /// <summary>
    /// 既存のファイルを置き換えられるか。読み取り専用・ほかのアプリが開いているファイルは、保存の最後の置き換え (<see cref="File.Move(string, string, bool)"/>) で失敗する。
    /// </summary>
    /// <remarks>
    /// 書き込み・共有なしで開けるかで確かめる (中身も更新日時も変えない)。
    /// </remarks>
    private static bool CanReplace(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AudioProcessingException NotWritable(string outPath, string userMessage, string suggestedSolution) => new(
        ErrorCodes.Audio.AudioFileExportFailed,
        $"Output path is not writable: {outPath}",
        userMessage,
        suggestedSolution);

    /// <remarks>
    /// <para>一時ファイル (<see cref="DiskCacheStore.TemporarySuffix"/>) に書いてから置き換える。直接書くと、途中で失敗・中断したときに
    /// 途中までのファイルが残り、既存のファイルも壊れる。一時ファイルは失敗しても残さない。</para>
    /// <para>一時ファイルの名前は固定なので、呼び出し元は <see cref="SpeechLock"/> の中で呼ぶ (<see cref="TextToSpeechProcessor"/>)。
    /// 同じ <c>--out</c> の実行どうしが同時に保存すると、互いの一時ファイルを壊したり消したりする。
    /// 名前を実行ごとに変えても、同じファイルへの置き換え (<see cref="File.Move(string, string, bool)"/>) が同時だと失敗する。</para>
    /// </remarks>
    private async Task WriteOutputFileAsync(byte[] audioData, string outPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outPath);
        if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = outPath + DiskCacheStore.TemporarySuffix;
        try
        {
            await File.WriteAllBytesAsync(tempPath, audioData, cancellationToken);
            File.Move(tempPath, outPath, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(tempPath);
        }
        _logger.LogDebug("音声ファイルを書き込みました: {OutPath}, サイズ: {Size} bytes", outPath, audioData.Length);
    }

    /// <summary>
    /// 一時ファイルを消す (置き換えた後は無いので何もしない。<see cref="File.Delete"/> はファイルが無くても例外にならない)。
    /// </summary>
    /// <remarks>
    /// 失敗は Warning に留める。保存の失敗の後に呼ばれたとき、消せなかった例外で元の失敗の理由を上書きしないため。
    /// </remarks>
    private void DeleteTemporaryFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "一時ファイルを削除できませんでした: {Path}", tempPath);
        }
    }
}
