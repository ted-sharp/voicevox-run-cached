using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class FillerGateTests
{
    [Test]
    public async Task AfterClose_DoesNotLoad()
    {
        // 回帰: SpeechLock を手放した後にフィラーのフォルダを読むと、ほかのプロセスの --clear の削除が共有違反で失敗する
        var loads = 0;
        using var gate = new FillerGate(_ =>
        {
            loads++;
            return Task.FromResult<byte[]?>([1]);
        });

        await Assert.That(await gate.GetAsync(CancellationToken.None)).IsNotNull();
        await gate.CloseAsync();

        await Assert.That(await gate.GetAsync(CancellationToken.None)).IsNull();
        await Assert.That(loads).IsEqualTo(1);
    }

    [Test]
    public async Task Close_WaitsForLoadInProgress()
    {
        // 読み込み中に閉じても、読み終わるまで戻らない (戻った後にフォルダに触れないように)
        var loading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var gate = new FillerGate(async _ =>
        {
            loading.SetResult();
            await release.Task;
            return [1];
        });

        var get = gate.GetAsync(CancellationToken.None);
        await loading.Task;
        var close = gate.CloseAsync();
        await Task.Delay(100);
        var closedBeforeLoadFinished = close.IsCompleted;

        release.SetResult();
        await close;

        await Assert.That(closedBeforeLoadFinished).IsFalse();
        await Assert.That(await get).IsNotNull();
    }
}
