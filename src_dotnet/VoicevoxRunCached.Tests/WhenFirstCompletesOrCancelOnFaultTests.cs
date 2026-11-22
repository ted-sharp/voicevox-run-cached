using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// TextToSpeechProcessor.WhenFirstCompletesOrCancelOnFaultAsync: 合成 (producer) が終わるまで待ち、その前に片方が失敗したら、もう一方をすぐ止める。
/// </summary>
public class WhenFirstCompletesOrCancelOnFaultTests
{
    [Test]
    public async Task PlaybackFaults_CancelsProducerAndThrows()
    {
        // 回帰: Task.WhenAll で待っていたため、再生が失敗しても producer が全セグメントを合成し終えるまで戻らなかった
        using var cts = new CancellationTokenSource();
        var producer = Task.Delay(Timeout.Infinite, cts.Token); // 止められるまで終わらない
        var playback = Task.FromException(new InvalidOperationException("device error"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TextToSpeechProcessor.WhenFirstCompletesOrCancelOnFaultAsync(producer, playback, cts).WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.That(producer.IsCanceled).IsTrue();
    }

    [Test]
    public async Task ProducerFaults_CancelsPlaybackAndThrows()
    {
        using var cts = new CancellationTokenSource();
        var producer = Task.FromException(new InvalidOperationException("engine error"));
        var playback = Task.Delay(Timeout.Infinite, cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TextToSpeechProcessor.WhenFirstCompletesOrCancelOnFaultAsync(producer, playback, cts).WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.That(playback.IsCanceled).IsTrue();
    }

    [Test]
    public async Task ProducerFaultsAfterPlaybackCompleted_Throws()
    {
        using var cts = new CancellationTokenSource();
        var producer = FailLaterAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TextToSpeechProcessor.WhenFirstCompletesOrCancelOnFaultAsync(producer, Task.CompletedTask, cts).WaitAsync(TimeSpan.FromSeconds(10)));

        static async Task FailLaterAsync()
        {
            await Task.Delay(50);
            throw new InvalidOperationException("engine error");
        }
    }

    [Test]
    public async Task ProducerCompletes_ReturnsWithoutWaitingForPlayback()
    {
        // 合成が終われば SpeechLock を手放す (再生の終わりを待たない)。再生は止めない
        using var cts = new CancellationTokenSource();
        var playback = Task.Delay(Timeout.Infinite, cts.Token);

        await TextToSpeechProcessor.WhenFirstCompletesOrCancelOnFaultAsync(Task.Delay(50), playback, cts).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(playback.IsCompleted).IsFalse();
        await Assert.That(cts.IsCancellationRequested).IsFalse();
    }
}
