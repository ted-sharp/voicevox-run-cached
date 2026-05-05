using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げ関連のコマンド処理を行うクラス
/// </summary>
public class TextToSpeechCommandHandler
{
    private readonly ILogger _logger;
    private readonly TextToSpeechProcessor _processor;

    public TextToSpeechCommandHandler(TextToSpeechProcessor processor, ILogger logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> HandleTextToSpeechAsync(VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _processor.ProcessTextToSpeechAsync(
                request, noCache, cacheOnly, verbose, outPath, noPlay, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テキスト読み上げ処理中にエラーが発生しました");
            ConsoleHelper.WriteError($"Error: {ex.Message}", _logger);
            return 1;
        }
    }
}
