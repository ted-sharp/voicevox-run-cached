using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// スピーカー関連のコマンド処理を行うクラス
/// </summary>
public class SpeakerCommandHandler
{
    private readonly SpeechEngineProvider _engines;
    private readonly SpeechLock _speechLock;
    private readonly ILogger _logger;

    public SpeakerCommandHandler(SpeechEngineProvider engines, SpeechLock speechLock, ILogger logger)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _speechLock = speechLock ?? throw new ArgumentNullException(nameof(speechLock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="engine">一覧を出すエンジン (<c>--engine</c>。省略時は <c>Synthesis.Engine</c>)</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <remarks>
    /// エンジンを自動起動しうるので、読み上げ・<c>--init</c> と同じ <see cref="SpeechLock"/> で直列化する。
    /// ロックの外だと、読み上げと同時にエンジンを二重に起動したり、<c>KeepEngineRunning: false</c> で自分が起動したエンジンを
    /// 終了時に止めて、それを使っている読み上げを合成の途中で失敗させたりするため。
    /// </remarks>
    public Task<int> HandleSpeakersAsync(SpeechEngineKind engine, CancellationToken cancellationToken = default)
        => _speechLock.RunAsync(() => ListSpeakersAsync(engine, cancellationToken), cancellationToken);

    private async Task<int> ListSpeakersAsync(SpeechEngineKind engine, CancellationToken cancellationToken)
    {
        var speakers = await _engines.Get(engine).GetSpeakersAsync(cancellationToken);
        _logger.LogInformation("スピーカー一覧を取得しました - エンジン: {Engine}, 件数: {Count}", engine, speakers.Count);

        ConsoleHelper.WriteLine("Available speakers:");
        foreach (var speaker in speakers)
        {
            ConsoleHelper.WriteLine(speaker.Version is { } version ? $"  {speaker.Name} (v{version})" : $"  {speaker.Name}");
            foreach (var voice in speaker.Voices)
            {
                ConsoleHelper.WriteLine($"    ID: {voice.Id} - {voice.Name}");
            }
            ConsoleHelper.WriteLine("");
        }
        return 0;
    }
}
