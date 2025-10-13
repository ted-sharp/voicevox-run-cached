using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Commands;

namespace VoicevoxRunCached.Services;

/// <summary>
/// コマンド処理の統合制御を行うクラス
/// 具体的な実装は各専門クラスに委譲します
/// </summary>
public class CommandHandler
{
    private readonly BenchmarkCommandHandler _benchmarkHandler;
    private readonly CacheCommandHandler _cacheHandler;
    private readonly DeviceCommandHandler _deviceHandler;
    private readonly InitCommandHandler _initHandler;
    private readonly ILogger _logger;
    private readonly AppSettings _settings;
    private readonly SpeakerCommandHandler _speakerHandler;
    private readonly TextToSpeechCommandHandler _ttsHandler;

    public CommandHandler(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 各専門クラスのインスタンスを作成
        _speakerHandler = new SpeakerCommandHandler(settings, logger);
        _deviceHandler = new DeviceCommandHandler(settings, logger);
        _initHandler = new InitCommandHandler(settings, logger);
        _cacheHandler = new CacheCommandHandler(settings, logger);
        _benchmarkHandler = new BenchmarkCommandHandler(settings, logger);
        _ttsHandler = new TextToSpeechCommandHandler(settings, logger);
    }

    /// <summary>
    /// スピーカー一覧の取得・表示を行います
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleSpeakersAsync()
    {
        return await _speakerHandler.HandleSpeakersAsync();
    }

    /// <summary>
    /// デバイス一覧の取得・表示を行います
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>処理結果の終了コード</returns>
    public int HandleDevices(string[] args)
    {
        return _deviceHandler.HandleDevices(args);
    }

    /// <summary>
    /// フィラーキャッシュの初期化を行います
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleInitAsync()
    {
        return await _initHandler.HandleInitAsync();
    }

    /// <summary>
    /// キャッシュのクリアを行います
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleClearCacheAsync()
    {
        return await _cacheHandler.HandleClearCacheAsync();
    }

    /// <summary>
    /// パフォーマンスベンチマークを実行します
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleBenchmarkAsync()
    {
        return await _benchmarkHandler.HandleBenchmarkAsync();
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
        return await _ttsHandler.HandleTextToSpeechAsync(request, noCache, cacheOnly, verbose, outPath, noPlay, cancellationToken);
    }
}
