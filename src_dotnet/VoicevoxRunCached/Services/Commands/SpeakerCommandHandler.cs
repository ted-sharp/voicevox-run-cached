using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// スピーカー関連のコマンド処理を行うクラス
/// </summary>
public class SpeakerCommandHandler
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly SpeechLock _speechLock;
    private readonly ILogger _logger;

    public SpeakerCommandHandler(VoiceVoxEngineManager engineManager, VoiceVoxApiClient apiClient, SpeechLock speechLock, ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _speechLock = speechLock ?? throw new ArgumentNullException(nameof(speechLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <remarks>
    /// エンジンを自動起動しうるので、読み上げ・<c>--init</c> と同じ <see cref="SpeechLock"/> で直列化する。
    /// ロックの外だと、読み上げと同時にエンジンを二重に起動したり、<c>KeepEngineRunning: false</c> で自分が起動したエンジンを
    /// 終了時に止めて、それを使っている読み上げを合成の途中で失敗させたりするため。
    /// </remarks>
    public Task<int> HandleSpeakersAsync(CancellationToken cancellationToken = default)
        => _speechLock.RunAsync(() => ListSpeakersAsync(cancellationToken), cancellationToken);

    private async Task<int> ListSpeakersAsync(CancellationToken cancellationToken)
    {
        await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

        var speakers = await _apiClient.GetSpeakersAsync(cancellationToken);
        _logger.LogInformation("スピーカー一覧を取得しました - 件数: {Count}", speakers.Count);

        ConsoleHelper.WriteLine("Available speakers:");
        foreach (var speaker in speakers)
        {
            ConsoleHelper.WriteLine($"  {speaker.Name} (v{speaker.Version})");
            foreach (var style in speaker.Styles)
            {
                ConsoleHelper.WriteLine($"    ID: {style.Id} - {style.Name}");
            }
            ConsoleHelper.WriteLine("");
        }
        return 0;
    }
}
