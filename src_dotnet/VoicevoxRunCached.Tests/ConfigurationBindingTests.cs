using Microsoft.Extensions.Configuration;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// ApplicationBootstrap.BuildAppSettings と同じく既存インスタンスへ Bind したときの挙動。
/// </summary>
public class ConfigurationBindingTests
{
    [Test]
    public async Task FillerTexts_AreReplacedNotAppended()
    {
        // 回帰: 以前は FillerSettings に既定の 6 件が初期値として入っており、
        // ConfigurationBinder が設定値を追記して 12 件 (重複あり) になっていた
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Filler:FillerTexts:0"] = "えーと、",
                ["Filler:FillerTexts:1"] = "うーん、",
            })
            .Build();

        var settings = new AppSettings();
        configuration.GetSection("Filler").Bind(settings.Filler);

        await Assert.That(settings.Filler.FillerTexts).IsEquivalentTo(["えーと、", "うーん、"]);
    }

    [Test]
    public async Task ShippedAppSettings_FillerTextsHaveNoDuplicates()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        var settings = new AppSettings();
        configuration.GetSection("Filler").Bind(settings.Filler);

        await Assert.That(settings.Filler.FillerTexts).IsNotEmpty();
        await Assert.That(settings.Filler.FillerTexts.Distinct().Count()).IsEqualTo(settings.Filler.FillerTexts.Length);
    }
}
