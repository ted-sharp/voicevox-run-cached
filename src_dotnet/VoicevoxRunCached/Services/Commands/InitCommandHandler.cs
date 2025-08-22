using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// 初期化関連のコマンド処理を行うクラス
/// </summary>
public class InitCommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public InitCommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            var cacheManager = new AudioCacheManager(this._settings.Cache);
            var fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);

            ConsoleHelper.WriteLine("Initializing filler cache...", this._logger);
            await fillerManager.InitializeFillerCacheAsync(this._settings);
            ConsoleHelper.WriteSuccess("Filler cache initialized", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error initializing filler cache: {ex.Message}", this._logger);
            return 1;
        }
    }
}
