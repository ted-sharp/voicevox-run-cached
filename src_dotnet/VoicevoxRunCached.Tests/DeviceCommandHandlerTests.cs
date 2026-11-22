using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services.Commands;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

public class DeviceCommandHandlerTests
{
    [Test]
    public async Task AllDeviceNumbersText_SplitsIntoEachDeviceNumberText()
    {
        // devices --speak はまとめて合成してキャッシュし、デバイスごとに --cache-only で鳴らす。
        // まとめた文を分けた結果がデバイスごとの文と違うと、キャッシュに無いとして失敗する
        int[] devices = [0, 1, 2, 10];
        var segments = await new TextSegmentProcessor(NullLogger.Instance).ProcessTextAsync(DeviceCommandHandler.AllDeviceNumbersText(devices));

        await Assert.That(segments.Select(s => s.Text)).IsEquivalentTo(devices.Select(DeviceCommandHandler.DeviceNumberText));
    }
}
