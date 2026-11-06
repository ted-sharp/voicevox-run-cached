using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// TextToSpeechProcessor.WhenAllOrCancelOnFaultAsync: 合成 (producer) と再生の片方が失敗したら、もう一方をすぐ止める。
/// </summary>
public class WhenAllOrCancelOnFaultTests
{
    [Test]
    public async Task OneFaults_CancelsOtherAndThrows()
    {
        // 回帰: Task.WhenAll で待っていたため、再生が失敗しても producer が全セグメントを合成し終えるまで戻らなかった
        using var cts = new CancellationTokenSource();
        var producer = Task.Delay(Timeout.Infinite, cts.Token); // 止められるまで終わらない
        var playback = Task.FromException(new InvalidOperationException("device error"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TextToSpeechProcessor.WhenAllOrCancelOnFaultAsync(producer, playback, cts).WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.That(producer.IsCanceled).IsTrue();
    }

    [Test]
    public async Task LaterTaskFaults_Throws()
    {
        using var cts = new CancellationTokenSource();
        var producer = Task.CompletedTask;
        var playback = FailLaterAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TextToSpeechProcessor.WhenAllOrCancelOnFaultAsync(producer, playback, cts).WaitAsync(TimeSpan.FromSeconds(10)));

        static async Task FailLaterAsync()
        {
            await Task.Delay(50);
            throw new InvalidOperationException("device error");
        }
    }

    [Test]
    public async Task BothSucceed_DoesNotCancel()
    {
        using var cts = new CancellationTokenSource();

        await TextToSpeechProcessor.WhenAllOrCancelOnFaultAsync(Task.Delay(50), Task.CompletedTask, cts);

        await Assert.That(cts.IsCancellationRequested).IsFalse();
    }
}
