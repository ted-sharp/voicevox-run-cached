using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Tests;

public class ArgumentParserTests
{
    private static AppSettings DefaultSettings() => new()
    {
        VoiceVox = new VoiceVoxSettings { DefaultSpeaker = 1 }
    };

    [Test]
    public async Task Parse_EmptyArgs_RequestIsNull()
    {
        var (request, _) = ArgumentParser.Parse(Array.Empty<string>(), DefaultSettings());

        await Assert.That(request).IsNull();
    }

    [Test]
    public async Task Parse_TextOnly_UsesDefaultSpeaker()
    {
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 7;

        var (request, _) = ArgumentParser.Parse(new[] { "こんにちは" }, settings);

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Text).IsEqualTo("こんにちは");
        await Assert.That(request.SpeakerId).IsEqualTo(7);
    }

    [Test]
    public async Task Parse_LongSpeakerOption()
    {
        var (request, _) = ArgumentParser.Parse(new[] { "text", "--speaker", "3" }, DefaultSettings());

        await Assert.That(request!.SpeakerId).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_ShortSpeakerOption()
    {
        var (request, _) = ArgumentParser.Parse(new[] { "text", "-s", "5" }, DefaultSettings());

        await Assert.That(request!.SpeakerId).IsEqualTo(5);
    }

    [Test]
    public async Task Parse_ConcatenatedShortSpeaker()
    {
        var (request, _) = ArgumentParser.Parse(new[] { "text", "-s9" }, DefaultSettings());

        await Assert.That(request!.SpeakerId).IsEqualTo(9);
    }

    [Test]
    public async Task Parse_SpeedPitchVolume()
    {
        var (request, _) = ArgumentParser.Parse(
            new[] { "text", "--speed", "1.5", "--pitch", "0.1", "--volume", "0.8" },
            DefaultSettings());

        await Assert.That(request!.Speed).IsEqualTo(1.5);
        await Assert.That(request.Pitch).IsEqualTo(0.1);
        await Assert.That(request.Volume).IsEqualTo(0.8);
    }

    [Test]
    public async Task Parse_FloatsAreInvariantCulture()
    {
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var (request, _) = ArgumentParser.Parse(new[] { "text", "--speed", "1.25" }, DefaultSettings());

            await Assert.That(request!.Speed).IsEqualTo(1.25);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Test]
    public async Task Parse_DetectsAllFlags()
    {
        var (_, options) = ArgumentParser.Parse(
            new[] { "text", "--no-cache", "--cache-only", "--verbose", "--no-play", "--out", "/tmp/out.mp3" },
            DefaultSettings());

        await Assert.That(options.NoCache).IsTrue();
        await Assert.That(options.CacheOnly).IsTrue();
        await Assert.That(options.Verbose).IsTrue();
        await Assert.That(options.NoPlay).IsTrue();
        await Assert.That(options.OutPath).IsEqualTo("/tmp/out.mp3");
    }

    [Test]
    public async Task Parse_ShortOutOption()
    {
        var (_, options) = ArgumentParser.Parse(new[] { "text", "-o", "out.wav" }, DefaultSettings());

        await Assert.That(options.OutPath).IsEqualTo("out.wav");
    }

    [Test]
    public async Task Parse_DefaultsAllFalse()
    {
        var (_, options) = ArgumentParser.Parse(new[] { "text" }, DefaultSettings());

        await Assert.That(options.NoCache).IsFalse();
        await Assert.That(options.CacheOnly).IsFalse();
        await Assert.That(options.Verbose).IsFalse();
        await Assert.That(options.NoPlay).IsFalse();
        await Assert.That(options.OutPath).IsNull();
    }

    [Test]
    public async Task Parse_OutEqualsForm_Recognized()
    {
        var (_, options) = ArgumentParser.Parse(new[] { "text", "--out=C:/tmp/out.mp3" }, DefaultSettings());
        await Assert.That(options.OutPath).IsEqualTo("C:/tmp/out.mp3");
    }

    [Test]
    public async Task Parse_LogLevelAndFormat_DoNotProduceWarning()
    {
        // --log-level / --log-format は ArgumentParser では skip されるが、未知扱いにはならない
        var (request, _) = ArgumentParser.Parse(
            new[] { "text", "--log-level", "debug", "--log-format", "json" },
            DefaultSettings());

        await Assert.That(request).IsNotNull();
        await Assert.That(request!.Text).IsEqualTo("text");
    }

    [Test]
    public async Task ParseTestCommand_AppliesOptionsWithoutTextArg()
    {
        // --test 由来のフローでは args の先頭がテキストではなくオプション扱い
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 1;

        var (request, options) = ArgumentParser.ParseTestCommand(
            "テストメッセージ",
            new[] { "--speaker", "3", "--speed", "1.25", "--out", "test.mp3" },
            settings);

        await Assert.That(request.Text).IsEqualTo("テストメッセージ");
        await Assert.That(request.SpeakerId).IsEqualTo(3);
        await Assert.That(request.Speed).IsEqualTo(1.25);
        await Assert.That(options.OutPath).IsEqualTo("test.mp3");
    }

    [Test]
    [Arguments("--speed", "abc")]
    [Arguments("--speed", "0")]
    [Arguments("--speed", "-1")]
    [Arguments("--pitch", "x")]
    [Arguments("--volume", "-0.5")]
    [Arguments("--speaker", "1.5")]
    [Arguments("--speaker", "-1")]
    [Arguments("-s", "abc")]
    public async Task Parse_InvalidOptionValue_Throws(string option, string value)
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", option, value], DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("--speed")]
    [Arguments("--out")]
    [Arguments("-s")]
    public async Task Parse_OptionWithoutValue_Throws(string option)
    {
        await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", option], DefaultSettings())));
    }

    [Test]
    public async Task Parse_Flags_DoNotWarnAboutExpandedTrue()
    {
        // PreprocessArgs が --no-play → --no-play true に展開するため、
        // 直後の true を消費しないと "Unknown option 'true'" 警告が出ていた (回帰防止)
        var logger = new WarningCountingLogger();

        var (_, options) = ArgumentParser.Parse(
            ["text", "--no-play", "--no-cache", "--verbose", "--cache-only", "-s", "3"], DefaultSettings(), logger);

        await Assert.That(logger.WarningCount).IsEqualTo(0);
        await Assert.That(options.NoPlay).IsTrue();
        await Assert.That(options.NoCache).IsTrue();
        await Assert.That(options.Verbose).IsTrue();
        await Assert.That(options.CacheOnly).IsTrue();
    }

    private sealed class WarningCountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                WarningCount++;
        }
    }

    [Test]
    public async Task Parse_BoundaryValues_Accepted()
    {
        var (request, _) = ArgumentParser.Parse(
            ["text", "-s", "0", "--volume", "0", "--pitch", "-0.15", "--speed", "0.5"], DefaultSettings());

        await Assert.That(request!.SpeakerId).IsEqualTo(0);
        await Assert.That(request.Volume).IsEqualTo(0.0);
        await Assert.That(request.Pitch).IsEqualTo(-0.15);
        await Assert.That(request.Speed).IsEqualTo(0.5);
    }

    [Test]
    public async Task ParseTestCommand_EmptyOptions_UsesDefaults()
    {
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 5;

        var (request, options) = ArgumentParser.ParseTestCommand("hello", Array.Empty<string>(), settings);

        await Assert.That(request.Text).IsEqualTo("hello");
        await Assert.That(request.SpeakerId).IsEqualTo(5);
        await Assert.That(request.Speed).IsEqualTo(1.0);
        await Assert.That(options.OutPath).IsNull();
    }
}
