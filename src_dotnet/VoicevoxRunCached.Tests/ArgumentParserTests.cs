using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Tests;

public class ArgumentParserTests
{
    private static AppSettings DefaultSettings() => new()
    {
        VoiceVox = new VoiceVoxSettings { DefaultSpeaker = 1 }
    };

    [Test]
    public async Task ParseArguments_EmptyArgs_ReturnsNull()
    {
        var result = ArgumentParser.ParseArguments(Array.Empty<string>(), DefaultSettings());

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ParseArguments_TextOnly_UsesDefaultSpeaker()
    {
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 7;

        var result = ArgumentParser.ParseArguments(new[] { "こんにちは" }, settings);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Text).IsEqualTo("こんにちは");
        await Assert.That(result.SpeakerId).IsEqualTo(7);
    }

    [Test]
    public async Task ParseArguments_LongSpeakerOption()
    {
        var result = ArgumentParser.ParseArguments(new[] { "text", "--speaker", "3" }, DefaultSettings());

        await Assert.That(result!.SpeakerId).IsEqualTo(3);
    }

    [Test]
    public async Task ParseArguments_ShortSpeakerOption()
    {
        var result = ArgumentParser.ParseArguments(new[] { "text", "-s", "5" }, DefaultSettings());

        await Assert.That(result!.SpeakerId).IsEqualTo(5);
    }

    [Test]
    public async Task ParseArguments_ConcatenatedShortSpeaker()
    {
        var result = ArgumentParser.ParseArguments(new[] { "text", "-s9" }, DefaultSettings());

        await Assert.That(result!.SpeakerId).IsEqualTo(9);
    }

    [Test]
    public async Task ParseArguments_SpeedPitchVolume()
    {
        var result = ArgumentParser.ParseArguments(
            new[] { "text", "--speed", "1.5", "--pitch", "0.1", "--volume", "0.8" },
            DefaultSettings());

        await Assert.That(result!.Speed).IsEqualTo(1.5);
        await Assert.That(result.Pitch).IsEqualTo(0.1);
        await Assert.That(result.Volume).IsEqualTo(0.8);
    }

    [Test]
    public async Task ParseArguments_ParsesFloatsAsInvariantCulture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var result = ArgumentParser.ParseArguments(new[] { "text", "--speed", "1.25" }, DefaultSettings());

            await Assert.That(result!.Speed).IsEqualTo(1.25);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public async Task ParseTextToSpeechOptions_DetectsAllFlags()
    {
        var options = ArgumentParser.ParseTextToSpeechOptions(
            new[] { "text", "--no-cache", "--cache-only", "--verbose", "--no-play", "--out", "/tmp/out.mp3" });

        await Assert.That(options.NoCache).IsTrue();
        await Assert.That(options.CacheOnly).IsTrue();
        await Assert.That(options.Verbose).IsTrue();
        await Assert.That(options.NoPlay).IsTrue();
        await Assert.That(options.OutPath).IsEqualTo("/tmp/out.mp3");
    }

    [Test]
    public async Task ParseTextToSpeechOptions_ShortOutOption()
    {
        var options = ArgumentParser.ParseTextToSpeechOptions(new[] { "text", "-o", "out.wav" });

        await Assert.That(options.OutPath).IsEqualTo("out.wav");
    }

    [Test]
    public async Task ParseTextToSpeechOptions_DefaultsAllFalse()
    {
        var options = ArgumentParser.ParseTextToSpeechOptions(new[] { "text" });

        await Assert.That(options.NoCache).IsFalse();
        await Assert.That(options.CacheOnly).IsFalse();
        await Assert.That(options.Verbose).IsFalse();
        await Assert.That(options.NoPlay).IsFalse();
        await Assert.That(options.OutPath).IsNull();
    }

    [Test]
    public async Task ParseTextToSpeechOptions_OutEqualsForm_Recognized()
    {
        var options = ArgumentParser.ParseTextToSpeechOptions(new[] { "text", "--out=C:/tmp/out.mp3" });
        await Assert.That(options.OutPath).IsEqualTo("C:/tmp/out.mp3");
    }

    [Test]
    public async Task GetBoolOption_DetectsPresence()
    {
        await Assert.That(ArgumentParser.GetBoolOption(new[] { "text", "--verbose" }, "--verbose")).IsTrue();
        await Assert.That(ArgumentParser.GetBoolOption(new[] { "text" }, "--verbose")).IsFalse();
    }

    [Test]
    public async Task GetStringOption_ReturnsValueOrNull()
    {
        await Assert.That(ArgumentParser.GetStringOption(new[] { "text", "--log-level", "debug" }, "--log-level"))
            .IsEqualTo("debug");
        await Assert.That(ArgumentParser.GetStringOption(new[] { "text" }, "--log-level"))
            .IsNull();
    }

    [Test]
    public async Task ParseArguments_LogLevelAndFormat_DoNotProduceWarning()
    {
        // --log-level / --log-format は ArgumentParser では skip されるが、未知扱いにはならない
        var result = ArgumentParser.ParseArguments(
            new[] { "text", "--log-level", "debug", "--log-format", "json" },
            DefaultSettings());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Text).IsEqualTo("text");
    }
}
