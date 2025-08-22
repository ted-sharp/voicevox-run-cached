using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// VOICEVOXエンジンの管理を行うクラス
/// </summary>
public class EngineCoordinator
{
    private readonly VoiceVoxSettings _settings;
    private readonly ILogger _logger;

    public EngineCoordinator(VoiceVoxSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// VOICEVOXエンジンが動作していることを確認します
    /// </summary>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>エンジンが利用可能な場合はtrue</returns>
    public async Task<bool> EnsureEngineRunningAsync(CancellationToken cancellationToken = default)
    {
        var engineStartTime = DateTime.UtcNow;

        try
        {
            using var engineManager = new VoiceVoxEngineManager(this._settings);

            if (cancellationToken.IsCancellationRequested)
            {
                this._logger.LogWarning("エンジン確認がキャンセルされました");
                return false;
            }

            var isRunning = await engineManager.EnsureEngineRunningAsync();

            if (isRunning)
            {
                var elapsed = (DateTime.UtcNow - engineStartTime).TotalMilliseconds;
                this._logger.LogDebug("エンジン確認が完了しました: {Elapsed}ms", elapsed);
            }
            else
            {
                this._logger.LogError("VOICEVOXエンジンが利用できません");
            }

            return isRunning;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "エンジン確認中にエラーが発生しました");
            return false;
        }
    }

    /// <summary>
    /// エンジンの状態を取得します
    /// </summary>
    /// <returns>エンジンの状態情報</returns>
    public async Task<EngineStatus> GetEngineStatusAsync()
    {
        try
        {
            using var engineManager = new VoiceVoxEngineManager(this._settings);
            var isRunning = await engineManager.EnsureEngineRunningAsync();

            return new EngineStatus
            {
                IsRunning = isRunning,
                LastChecked = DateTime.UtcNow,
                Settings = this._settings
            };
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "エンジン状態の取得中にエラーが発生しました");
            return new EngineStatus
            {
                IsRunning = false,
                LastChecked = DateTime.UtcNow,
                Error = ex.Message
            };
        }
    }
}

/// <summary>
/// エンジンの状態情報
/// </summary>
public class EngineStatus
{
    public bool IsRunning { get; set; }
    public DateTime LastChecked { get; set; }
    public VoiceVoxSettings? Settings { get; set; }
    public string? Error { get; set; }
}
