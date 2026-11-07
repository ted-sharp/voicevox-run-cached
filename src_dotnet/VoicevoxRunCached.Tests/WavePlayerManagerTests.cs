using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

public class WavePlayerManagerTests
{
    // NAudio 2 の WaveOutEvent.Init と同じく、合計の遅延をバッファ数で割って切り上げる
    // (NAudio 3 で DesiredLatency が無くなり BufferMilliseconds を直接指定するようになったため)
    [Test]
    [Arguments(100, 3, 34)]
    [Arguments(300, 2, 150)]
    [Arguments(10, 10, 1)]
    [Arguments(1000, 1, 1000)]
    public async Task GetBufferMilliseconds_DividesLatencyByBuffersRoundingUp(int desiredLatency, int numberOfBuffers, int expected)
    {
        var settings = new AudioSettings { DesiredLatency = desiredLatency, NumberOfBuffers = numberOfBuffers };

        await Assert.That(WavePlayerManager.GetBufferMilliseconds(settings)).IsEqualTo(expected);
    }
}
