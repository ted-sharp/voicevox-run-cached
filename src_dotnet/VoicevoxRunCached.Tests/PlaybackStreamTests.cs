using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// PlaybackStream: 読み上げのあいだ再生デバイスに流し続ける音声。
/// 回帰: 文ごとに再生を止めて開き直していたので、その間に Bluetooth のデバイスが休み、2 文目以降の始めが切れていた。
/// </summary>
public class PlaybackStreamTests
{
    [Test]
    public async Task Read_NothingQueued_FillsWithSilence()
    {
        // 並んでいない間も無音で埋めて全部を返す (少なく返すと WaveOut が止まり、デバイスが休む)
        var stream = new PlaybackStream();
        var buffer = Enumerable.Repeat(1f, 100).ToArray();

        await Assert.That(stream.Read(buffer, 0, buffer.Length)).IsEqualTo(100);
        await Assert.That(buffer.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task Read_PlaysQueuedAudioInOrderThenSilence()
    {
        var stream = new PlaybackStream();
        var first = stream.Enqueue([1, 2, 3]);
        var second = stream.Enqueue([4, 5]);
        var buffer = new float[8];

        stream.Read(buffer, 0, buffer.Length);

        await Assert.That(buffer).IsEquivalentTo(new float[] { 1, 2, 3, 4, 5, 0, 0, 0 });
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Enqueue_CompletesOnlyAfterLastSampleIsRead()
    {
        var stream = new PlaybackStream();
        var played = stream.Enqueue([1, 2, 3, 4]);

        stream.Read(new float[3], 0, 3);
        await Assert.That(played.IsCompleted).IsFalse();

        stream.Read(new float[3], 0, 3);
        await played.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Fail_FaultsPendingAudio()
    {
        // デバイスのエラー・中止を待っている側に伝える (読まれないまま待ち続けない)
        var stream = new PlaybackStream();
        var reading = stream.Enqueue([1, 2, 3, 4]);
        var queued = stream.Enqueue([5]);
        stream.Read(new float[2], 0, 2);

        stream.Fail(new InvalidOperationException("device error"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reading);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        var buffer = new float[2];
        stream.Read(buffer, 0, 2);
        await Assert.That(buffer.All(s => s == 0)).IsTrue();
    }

    [Test]
    [Arguments(24000, 1)]
    [Arguments(48000, 1)]
    [Arguments(24000, 2)]
    public async Task Decode_ConvertsTo48kHzMono(int sampleRate, int channels)
    {
        // 合成 (24kHz) とキャッシュ (48kHz) の音声を 1 つの流れに並べるため、48kHz mono にそろえる
        using var reader = new AudioFormatDetector(NullLogger.Instance).CreateWaveStream(TestAudio.SineWav(sampleRate, 500, channels));

        var samples = PlaybackStream.Decode(reader.ToSampleProvider());

        await Assert.That(PlaybackStream.DurationOf(samples.Length).TotalMilliseconds).IsBetween(490, 510);
        await Assert.That(samples.Any(s => s != 0)).IsTrue();
    }

    [Test]
    public async Task Silence_HasDurationAt48kHz()
    {
        await Assert.That(PlaybackStream.Silence(TimeSpan.FromMilliseconds(120)).Length).IsEqualTo(5760);
    }
}
