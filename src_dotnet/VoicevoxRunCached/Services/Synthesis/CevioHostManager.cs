using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// CeVIO AI (ホスト) の起動と停止。<see cref="VoiceVoxEngineManager"/> の CeVIO 版。
/// </summary>
/// <remarks>
/// 自分で起動したホストだけを、<see cref="CevioSettings.KeepHostRunning"/> が false なら <see cref="StopHostIfNotKept"/> で閉じる。
/// 外部で起動済みだったホストには触れない。
/// </remarks>
public class CevioHostManager
{
    private readonly CevioSettings _settings;
    private readonly ICevioRemoteService _remote;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    // このプロセスがホストを起動したか。閉じる対象を自分が起動したものだけに絞るために使う
    private bool _startedByUs;

    public CevioHostManager(CevioSettings settings, ICevioRemoteService remote, ILogger logger, IClock clock)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// ホストが起動していなければ (<see cref="CevioSettings.AutoStartHost"/> なら) 起動し、使えるようになるまで待つ。
    /// </summary>
    /// <exception cref="VoicevoxRunCachedException">CeVIO AI を使えない (<see cref="ErrorCodes.Engine.EngineNotAvailable"/>)</exception>
    public async Task EnsureRunningOrThrowAsync(CancellationToken cancellationToken)
    {
        if (_remote.IsHostStarted)
        {
            return;
        }

        if (!_settings.AutoStartHost)
        {
            throw NotAvailable("CeVIO AI host is not started", "CeVIO AI が起動していません。",
                "CeVIO AI を起動するか、appsettings.json の Cevio.AutoStartHost を true にしてください。");
        }

        var hostPath = ResolveHostPath(_settings.HostPath, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (!File.Exists(hostPath))
        {
            throw NotAvailable($"CeVIO AI not found: {hostPath}", $"CeVIO AI が見つかりません: {hostPath}",
                "appsettings.json の Cevio.HostPath に CeVIO AI.exe のパスを設定してください。");
        }

        _logger.LogInformation("CeVIO AI が起動していません。起動します: {HostPath}", hostPath);
        using var spinner = new ProgressSpinner("Starting CeVIO AI...");

        // UseShellExecute = false だと、本ツールの継承可能なハンドル (Hooks から呼ばれたときの stdout / stderr のパイプ) を
        // CeVIO AI が引き継ぎ、常駐させると Hooks がタイムアウトまで待たされる (VoiceVoxEngineManager と同じ理由)。
        // CeVIO AI (9.1) は WindowStyle (Hidden / Minimized) を無視して常にウィンドウを出す。
        // Minimized にしておくのは、効く版になっても邪魔にならず、ウィンドウを戻して閉じられるようにするため (Hidden だと閉じられない)
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = hostPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Minimized,
            WorkingDirectory = Path.GetDirectoryName(hostPath) ?? Environment.CurrentDirectory,
        });
        _startedByUs = true;

        var timeout = TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds);
        var startTime = _clock.UtcNow;
        while (_clock.UtcNow - startTime < timeout)
        {
            if (_remote.IsHostStarted)
            {
                _logger.LogInformation("CeVIO AI が起動しました");
                return;
            }
            // 起動に失敗して終わった (ライセンス等)。タイムアウトまで待たない
            if (process is { HasExited: true })
            {
                _startedByUs = false;
                throw NotAvailable($"CeVIO AI exited during startup (exit code {process.ExitCode})", "CeVIO AI の起動に失敗しました。",
                    "CeVIO AI を手動で起動して、エラーが出ないか確認してください。");
            }
            await Task.Delay(AudioConstants.EngineStartupPollIntervalMs, cancellationToken);
        }

        throw NotAvailable("CeVIO AI startup timed out", $"CeVIO AI が {_settings.StartupTimeoutSeconds} 秒以内に起動しませんでした。",
            "CeVIO AI を手動で起動して確認するか、appsettings.json の Cevio.StartupTimeoutSeconds を延ばしてください。");
    }

    /// <summary>
    /// <see cref="CevioSettings.KeepHostRunning"/> が false なら、このプロセスが起動したホストを閉じる。
    /// </summary>
    /// <remarks>
    /// <see cref="SpeechLock"/> がプロセス間ロックを解放する前に (<see cref="ISpeechEngine.StopIfNotKept"/> から) 呼ぶ。
    /// </remarks>
    public void StopHostIfNotKept()
    {
        if (_settings.KeepHostRunning || !_startedByUs)
        {
            return;
        }

        _startedByUs = false;
        try
        {
            _remote.CloseHost();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CeVIO AI を閉じられませんでした");
        }
    }

    /// <summary>
    /// 起動する <c>CeVIO AI.exe</c>。設定が空なら既定のインストール先。
    /// </summary>
    public static string ResolveHostPath(string configuredPath, string programFilesPath)
        => String.IsNullOrEmpty(configuredPath)
            ? Path.Combine(programFilesPath, "CeVIO", "CeVIO AI", "CeVIO AI.exe")
            : configuredPath;

    private static VoicevoxRunCachedException NotAvailable(string message, string userMessage, string suggestedSolution) => new(
        errorCode: ErrorCodes.Engine.EngineNotAvailable,
        message: message,
        userMessage: userMessage,
        suggestedSolution: suggestedSolution);
}
