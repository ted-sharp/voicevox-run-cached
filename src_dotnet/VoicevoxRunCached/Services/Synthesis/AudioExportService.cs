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

    private async Task WriteOutputFileAsync(byte[] audioData, string outPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outPath);
        if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(outPath, audioData, cancellationToken);
        _logger.LogDebug("音声ファイルを書き込みました: {OutPath}, サイズ: {Size} bytes", outPath, audioData.Length);
    }
}
