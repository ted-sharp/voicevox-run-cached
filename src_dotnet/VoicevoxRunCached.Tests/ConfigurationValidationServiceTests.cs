using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 設定検証エラーは Serilog 構成前に発生し UserMessage しか表示されないため、
/// どの項目が不正かが UserMessage に含まれることを確認する。
/// </summary>
public class ConfigurationValidationServiceTests
{
    [Test]
    public async Task InvalidSettings_UserMessageNamesTheProperty()
    {
        var settings = new AppSettings { Audio = new AudioSettings { Volume = 1.2 } };

        var ex = await Assert.ThrowsAsync<ConfigurationException>(() =>
        {
            new ConfigurationValidationService().ValidateConfiguration(settings);
            return Task.CompletedTask;
        });

        await Assert.That(ex!.UserMessage).Contains("Volume");
        await Assert.That(ex.ErrorCode).IsEqualTo(ErrorCodes.Configuration.InvalidSettings);
    }
}
