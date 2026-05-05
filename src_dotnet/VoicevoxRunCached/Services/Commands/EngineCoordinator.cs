using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// VOICEVOXエンジンの管理を行うクラス
/// </summary>
public class EngineCoordinator
{
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly ILogger _logger;
    private readonly VoiceVoxSettings _settings;

    public EngineCoordinator(VoiceVoxEngineManager engineManager, VoiceVoxSettings settings, ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> EnsureEngineRunningAsync(CancellationToken cancellationToken = default)
    {
        var engineStartTime = DateTime.UtcNow;

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("エンジン確認がキャンセルされました");
                return false;
            }

            var isRunning = await _engineManager.EnsureEngineRunningAsync();

            if (isRunning)
            {
                var elapsed = (DateTime.UtcNow - engineStartTime).TotalMilliseconds;
                _logger.LogDebug("エンジン確認が完了しました: {Elapsed}ms", elapsed);
            }
            else
            {
                _logger.LogError("VOICEVOXエンジンが利用できません");
            }

            return isRunning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "エンジン確認中にエラーが発生しました");
            return false;
        }
    }

    public async Task<EngineStatus> GetEngineStatusAsync()
    {
        try
        {
            var isRunning = await _engineManager.EnsureEngineRunningAsync();

            return new EngineStatus
            {
                IsRunning = isRunning,
                LastChecked = DateTime.UtcNow,
                Settings = _settings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "エンジン状態の取得中にエラーが発生しました");
            return new EngineStatus
            {
                IsRunning = false,
                LastChecked = DateTime.UtcNow,
                Error = ex.Message
            };
        }
    }
}

public class EngineStatus
{
    public bool IsRunning { get; set; }
    public DateTime LastChecked { get; set; }
    public VoiceVoxSettings? Settings { get; set; }
    public string? Error { get; set; }
}
