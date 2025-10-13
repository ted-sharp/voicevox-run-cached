using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 初期化関連のコマンド処理を行うクラス
/// </summary>
public class InitCommandHandler
{
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    public InitCommandHandler(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// フィラーキャッシュの初期化を行います
    /// </summary>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> HandleInitAsync()
    {
        try
        {
            // VOICEVOXエンジンが動作していることを確認
            using var engineManager = new VoiceVoxEngineManager(_settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
                return 1;
            }

            var cacheManager = new AudioCacheManager(_settings.Cache);
            var fillerManager = new FillerManager(_settings.Filler, cacheManager, _settings.VoiceVox.DefaultSpeaker);

            ConsoleHelper.WriteLine("Initializing filler cache...", _logger);
            await fillerManager.InitializeFillerCacheAsync(_settings);
            ConsoleHelper.WriteSuccess("Filler cache initialized", _logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error initializing filler cache: {ex.Message}", _logger);
            return 1;
        }
    }
}
