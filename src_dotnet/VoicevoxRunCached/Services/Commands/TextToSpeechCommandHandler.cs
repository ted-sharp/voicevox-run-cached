using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げ関連のコマンド処理を行うクラス
/// </summary>
public class TextToSpeechCommandHandler
{
    private readonly ILogger _logger;
    private readonly TextToSpeechProcessor _processor;

    public TextToSpeechCommandHandler(AppSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processor = new TextToSpeechProcessor(settings, logger);
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
