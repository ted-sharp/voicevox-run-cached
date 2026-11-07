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
            new[] { "text", "--no-cache", "--cache-only", "--verbose", "--no-play", "--out", "/tmp/out.opus" },
            DefaultSettings());

        await Assert.That(options.NoCache).IsTrue();
        await Assert.That(options.CacheOnly).IsTrue();
        await Assert.That(options.Verbose).IsTrue();
        await Assert.That(options.NoPlay).IsTrue();
        await Assert.That(options.OutPath).IsEqualTo("/tmp/out.opus");
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
        var (_, options) = ArgumentParser.Parse(new[] { "text", "--out=C:/tmp/out.opus" }, DefaultSettings());
        await Assert.That(options.OutPath).IsEqualTo("C:/tmp/out.opus");
    }

    [Test]
    [Arguments("--log-level", "debug", "--log-format", "json")]
    [Arguments("--log-level=debug", "--log-format=json", "-s", "3")]
    public async Task Parse_LogLevelAndFormat_DoNotProduceWarning(string a, string b, string c, string d)
    {
        // --log-level / --log-format は ConfigurationManager 側で反映するので ArgumentParser では skip する。
        // = 形式も含め、未知オプション扱いの警告を出さないこと
        var logger = new WarningCountingLogger();
        var (request, _) = ArgumentParser.Parse(["text", a, b, c, d], DefaultSettings(), logger);

        await Assert.That(request!.Text).IsEqualTo("text");
        await Assert.That(logger.WarningCount).IsEqualTo(0);
    }

    [Test]
    public async Task ParseTestCommand_AppliesOptionsWithoutTextArg()
    {
        // --test 由来のフローでは args の先頭がテキストではなくオプション扱い
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 1;

        var (request, options) = ArgumentParser.ParseTestCommand(
            "テストメッセージ",
            new[] { "--speaker", "3", "--speed", "1.25", "--out", "test.opus" },
            settings);

        await Assert.That(request.Text).IsEqualTo("テストメッセージ");
        await Assert.That(request.SpeakerId).IsEqualTo(3);
        await Assert.That(request.Speed).IsEqualTo(1.25);
        await Assert.That(options.OutPath).IsEqualTo("test.opus");
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
    public async Task Parse_OutFollowedByOption_Throws()
    {
        // --out の値を書き忘れたとき "--no-play" という名前のファイルを作らない
        await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", "--out", "--no-play"], DefaultSettings())));
    }

    [Test]
    public async Task Parse_SpeedRoundingToZero_Throws()
    {
        // 0.004 は F2 で 0.00 になり、実際には speed 0 で合成されてしまう
        await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", "--speed", "0.004"], DefaultSettings())));
    }

    [Test]
    [Arguments("out.mp3")]
    [Arguments("out.txt")]
    [Arguments("out")]
    public async Task Parse_OutUnsupportedExtension_ThrowsInvalidArguments(string path)
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", "--out", path], DefaultSettings())));
        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);

        await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", $"--out={path}"], DefaultSettings())));
    }

    [Test]
    [Arguments("out.wav")]
    [Arguments("OUT.OPUS")]
    public async Task Parse_OutSupportedExtension_Accepted(string path)
    {
        var (_, options) = ArgumentParser.Parse(["text", "--out", path], DefaultSettings());
        await Assert.That(options.OutPath).IsEqualTo(path);
    }

    [Test]
    [Arguments("-sample")]
    [Arguments("--hello")]
    [Arguments("-s3")]
    public async Task Parse_TextStartingWithHyphen_IsKeptVerbatim(string text)
    {
        var (request, _) = ArgumentParser.Parse([text, "-s", "3"], DefaultSettings());

        await Assert.That(request!.Text).IsEqualTo(text);
        await Assert.That(request.SpeakerId).IsEqualTo(3);
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

    [Test]
    public async Task Parse_Refresh_SetsFlagWithoutWarning()
    {
        var logger = new WarningCountingLogger();

        var (_, options) = ArgumentParser.Parse(["text", "--refresh", "--no-play"], DefaultSettings(), logger);

        await Assert.That(options.Refresh).IsTrue();
        await Assert.That(options.NoCache).IsFalse();
        await Assert.That(logger.WarningCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("--Filler:Enabled=true")]
    [Arguments("--Filler:Enabled true")]
    [Arguments("--filler:enabled=true")]
    public async Task Parse_ConfigurationOverride_DoesNotWarn(string overrideArgs)
    {
        // 回帰: 設定の上書きは ConfigurationManager が反映するのに、読み上げでは Unknown option と (値の 'true' も) 警告していた
        var logger = new WarningCountingLogger();
        string[] args = ["text", .. overrideArgs.Split(' '), "--no-play"];

        var (_, options) = ArgumentParser.Parse(args, DefaultSettings(), logger);

        await Assert.That(logger.WarningCount).IsEqualTo(0);
        // 上書きの値だけを読み飛ばし、後ろのオプションは読む
        await Assert.That(options.NoPlay).IsTrue();
    }

    [Test]
    [Arguments(@"--output=C:\tmp\a.wav")]
    [Arguments(@"--out:C:\tmp\a.wav")]
    public async Task Parse_UnknownOptionWithColon_ThrowsInvalidArguments(string option)
    {
        // 回帰: ':' を含む引数をすべて設定の上書きとみなしていたため、ドライブ名入りの打ち間違いを黙って無視していた
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", option, "--no-play"], DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
        await Assert.That(ex.UserMessage).Contains(option);
    }

    [Test]
    [Arguments("--speeed 1.5")]
    [Arguments("--speed 1.5 --unknown")]
    [Arguments("-x")]
    [Arguments("世界")]
    public async Task Parse_UnexpectedArgument_ThrowsInvalidArguments(string extraArgs)
    {
        // 回帰: 不明な引数を Warning だけ出して読み飛ばし、既定値で合成・キャッシュして終了コード 0 になっていた
        // (本文の引用符忘れ「こんにちは 世界」も、後半を黙って捨てていた)
        string[] args = ["こんにちは", .. extraArgs.Split(' ')];

        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(args, DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    public async Task ParseTestCommand_UnknownOption_ThrowsInvalidArguments()
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.ParseTestCommand("hello", ["--speeed", "1.5"], DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("--log-level")]
    [Arguments("--log-format")]
    public async Task Parse_LogOptionWithoutValue_ThrowsInvalidArguments(string option)
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", option], DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("speakers")]
    [Arguments("devices")]
    [Arguments("--init")]
    [Arguments("--help")]
    [Arguments("--")]
    public async Task Parse_TextAfterSeparator_IsReadAsText(string text)
    {
        // コマンド名と同じ文字列は "-- <本文>" で読み上げる
        var (request, options) = ArgumentParser.Parse(["--", text, "-s", "3", "--no-play"], DefaultSettings());

        await Assert.That(request!.Text).IsEqualTo(text);
        await Assert.That(request.SpeakerId).IsEqualTo(3);
        await Assert.That(options.NoPlay).IsTrue();
    }

    [Test]
    public async Task Parse_SeparatorWithoutText_RequestIsNull()
    {
        var (request, _) = ArgumentParser.Parse(["--"], DefaultSettings());

        await Assert.That(request).IsNull();
    }

    [Test]
    public async Task Parse_WithoutRefresh_RefreshIsFalse()
    {
        var (_, options) = ArgumentParser.Parse(["text"], DefaultSettings());

        await Assert.That(options.Refresh).IsFalse();
    }

    [Test]
    [Arguments("--no-cache")]
    [Arguments("--cache-only")]
    public async Task Parse_RefreshWithCacheOption_ThrowsInvalidArguments(string option)
    {
        // --refresh (合成し直して上書き) は、書かない --no-cache・合成しない --cache-only と両立しない
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.Parse(["text", "--refresh", option], DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("", 7)]
    [Arguments("-s 3", 3)]
    [Arguments("-s3", 3)]
    [Arguments("--speaker 4 --log-level debug", 4)]
    [Arguments("--log-level debug --Filler:Enabled=true", 7)]
    public async Task ParseInitSpeaker_ReadsSpeakerOrDefault(string args, int expected)
    {
        var settings = DefaultSettings();
        settings.VoiceVox.DefaultSpeaker = 7;

        var speakerId = ArgumentParser.ParseInitSpeaker(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), settings);

        await Assert.That(speakerId).IsEqualTo(expected);
    }

    [Test]
    [Arguments("-s abc")]
    [Arguments("-s -1")]
    [Arguments("-s")]
    public async Task ParseInitSpeaker_InvalidSpeaker_ThrowsInvalidArguments(string args)
    {
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.ParseInitSpeaker(args.Split(' '), DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("--speaker=3")]
    [Arguments("--speakr 3")]
    [Arguments("--speaker:3")]
    [Arguments("3")]
    public async Task ParseInitSpeaker_UnexpectedArgument_ThrowsInvalidArguments(string args)
    {
        // 回帰: 不明な引数を読み飛ばしていたため、--speaker=3 や打ち間違いでも既定の話者のフィラーを作って成功表示していた
        var ex = await Assert.ThrowsAsync<VoicevoxRunCachedException>(() =>
            Task.FromResult(ArgumentParser.ParseInitSpeaker(args.Split(' '), DefaultSettings())));

        await Assert.That(ex!.ErrorCode).IsEqualTo(ErrorCodes.General.InvalidArguments);
    }

    [Test]
    [Arguments("--log-level debug --Filler:Enabled=true", 1)]
    [Arguments("--log-format=json --Filler:Enabled true", 1)]
    [Arguments("-s 3 --log-level=debug", 3)]
    public async Task ParseInitSpeaker_LogAndConfigurationArgs_AreSkipped(string args, int expected)
    {
        // ログ設定と設定の上書き (--Section:Key=値 / --Section:Key 値) は設定の読み込み側で扱うので読み飛ばす
        var speakerId = ArgumentParser.ParseInitSpeaker(args.Split(' '), DefaultSettings());

        await Assert.That(speakerId).IsEqualTo(expected);
    }
}
