using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// デバイスの warm-up (Audio.PrepareDevice) は、再生することが決まってから始める。
/// </summary>
/// <remarks>
/// 実際に warm-up を走らせると音が鳴るので、始めないことだけを確認する。
/// </remarks>
public class AudioDeviceManagerTests
{
    private const string StartMessage = "デバイス事前準備を開始します";

    [Test]
    public async Task Construction_DoesNotStartPreparation()
    {
        // 回帰: コンストラクタで始めていたため、CommandRouter の依存として生成される
        // speakers / devices / --clear / --no-play でもウォームアップ音が鳴っていた
        var logger = new RecordingLogger();

        using (new AudioDeviceManager(new AudioSettings { PrepareDevice = true }, logger))
        {
        }

        await Assert.That(logger.Messages).DoesNotContain(StartMessage);
    }

    [Test]
    public async Task PrepareDeviceDisabled_StartPreparationDoesNothing()
    {
        var logger = new RecordingLogger();
        using var manager = new AudioDeviceManager(new AudioSettings { PrepareDevice = false }, logger);

        manager.StartPreparation();
        await manager.EnsureDeviceReadyAsync();

        await Assert.That(logger.Messages).DoesNotContain(StartMessage);
    }
}
