using FluentValidation;

namespace VoicevoxRunCached.Configuration.Validators;

// VoiceVoxSettings のバリデーター
public class VoiceVoxSettingsValidator : AbstractValidator<VoiceVoxSettings>
{
    public VoiceVoxSettingsValidator()
    {
        RuleFor(x => x.BaseUrl)
            .NotEmpty().WithMessage("BaseUrlは必須です")
            .Must(BeValidUrl).WithMessage("BaseUrlは有効なURLである必要があります");

        RuleFor(x => x.DefaultSpeaker)
            .GreaterThanOrEqualTo(0).WithMessage("DefaultSpeakerは0以上である必要があります");

        RuleFor(x => x.ConnectionTimeout)
            .InclusiveBetween(1, 300).WithMessage("ConnectionTimeoutは1-300秒の範囲である必要があります");

        RuleFor(x => x.StartupTimeoutSeconds)
            .InclusiveBetween(5, 600).WithMessage("StartupTimeoutSecondsは5-600秒の範囲である必要があります");

        RuleFor(x => x.EnginePath)
            .Must((settings, path) => String.IsNullOrEmpty(path) || File.Exists(path))
            .WithMessage("EnginePathが指定されている場合、ファイルが存在する必要があります");

        // 新しい検証ルール
        RuleFor(x => x.TimeoutMs)
            .InclusiveBetween(1000, 300000).WithMessage("TimeoutMsは1000-300000ミリ秒（5分）の範囲である必要があります");
    }

    private static bool BeValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }
}

// CacheSettings のバリデーター
public class CacheSettingsValidator : AbstractValidator<CacheSettings>
{
    public CacheSettingsValidator()
    {
        RuleFor(x => x.Directory)
            .NotEmpty().WithMessage("Cache Directoryは必須です")
            .Must(BeWritableDirectory).WithMessage("Cache Directoryは書き込み可能である必要があります");

        RuleFor(x => x.ExpirationDays)
            .InclusiveBetween(1, 3650).WithMessage("ExpirationDaysは1-3650日（10年）の範囲である必要があります");

        RuleFor(x => x.MaxSizeGB)
            .InclusiveBetween(0.1, 1000.0).WithMessage("MaxSizeGBは0.1-1000.0GBの範囲である必要があります");

        // 新しい検証ルール
        RuleFor(x => x.MemoryCacheSizeMB)
            .InclusiveBetween(1, 10000).WithMessage("MemoryCacheSizeMBは1-10000MBの範囲である必要があります");
    }

    private static bool BeWritableDirectory(string path)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;

            var testPath = Path.Combine(path, ".test");
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// AudioSettings のバリデーター
public class AudioSettingsValidator : AbstractValidator<AudioSettings>
{
    public AudioSettingsValidator()
    {
        RuleFor(x => x.OutputDevice)
            .GreaterThanOrEqualTo(-1).WithMessage("OutputDeviceは-1以上である必要があります");

        RuleFor(x => x.Volume)
            .InclusiveBetween(0.0, 2.0).WithMessage("Volumeは0.0-2.0の範囲である必要があります");

        RuleFor(x => x.PreparationDurationMs)
            .InclusiveBetween(50, 5000).WithMessage("PreparationDurationMsは50-5000ミリ秒の範囲である必要があります");

        RuleFor(x => x.PreparationVolume)
            .InclusiveBetween(0.0, 1.0).WithMessage("PreparationVolumeは0.0-1.0の範囲である必要があります");

        // 新しい検証ルール
        RuleFor(x => x.DesiredLatency)
            .InclusiveBetween(10, 1000).WithMessage("DesiredLatencyは10-1000ミリ秒の範囲である必要があります");

        RuleFor(x => x.NumberOfBuffers)
            .InclusiveBetween(1, 10).WithMessage("NumberOfBuffersは1-10の範囲である必要があります");
    }
}

// FillerSettings のバリデーター
public class FillerSettingsValidator : AbstractValidator<FillerSettings>
{
    public FillerSettingsValidator()
    {
        RuleFor(x => x.Directory)
            .NotEmpty().When(x => x.Enabled).WithMessage("Filler Directoryは必須です（フィラーが有効な場合）")
            .Must(BeWritableDirectory).When(x => x.Enabled).WithMessage("Filler Directoryは書き込み可能である必要があります");

        RuleFor(x => x.FillerTexts)
            .NotEmpty().When(x => x.Enabled).WithMessage("FillerTextsは空であってはいけません（フィラーが有効な場合）")
            .Must(texts => texts?.All(t => !String.IsNullOrWhiteSpace(t)) ?? true)
            .When(x => x.Enabled).WithMessage("FillerTextsの各要素は空文字列であってはいけません");

        // 新しい検証ルール
        RuleFor(x => x.MaxCacheSizeMB)
            .InclusiveBetween(1, 10000).When(x => x.Enabled).WithMessage("MaxCacheSizeMBは1-10000MBの範囲である必要があります");
    }

    private static bool BeWritableDirectory(string path)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;

            var testPath = Path.Combine(path, ".test");
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

// LoggingSettings のバリデーター
public class LoggingSettingsValidator : AbstractValidator<LoggingSettings>
{
    public LoggingSettingsValidator()
    {
        RuleFor(x => x.Level)
            .Must(BeValidLogLevel).WithMessage("Levelは有効なログレベル（trace, debug, info, warn, error, crit, none）である必要があります");

        RuleFor(x => x.Format)
            .Must(format => format == "simple" || format == "json")
            .WithMessage("Formatは'simple'または'json'である必要があります");

        // 新しい検証ルール
        RuleFor(x => x.MaxFileSizeMB)
            .InclusiveBetween(1, 1000).When(x => x.EnableFileLogging).WithMessage("MaxFileSizeMBは1-1000MBの範囲である必要があります");

        RuleFor(x => x.MaxFileCount)
            .InclusiveBetween(1, 100).When(x => x.EnableFileLogging).WithMessage("MaxFileCountは1-100の範囲である必要があります");
    }

    private static bool BeValidLogLevel(string level)
    {
        if (String.IsNullOrWhiteSpace(level))
            return false;

        var validLevels = new[] { "trace", "debug", "info", "warn", "error", "crit", "none" };
        return validLevels.Contains(level.ToLowerInvariant());
    }
}

// TestSettings のバリデーター
public class TestSettingsValidator : AbstractValidator<TestSettings>
{
    public TestSettingsValidator()
    {
        RuleFor(x => x.Message)
            .NotEmpty().WithMessage("Test Messageは必須です")
            .MaximumLength(1000).WithMessage("Test Messageは1000文字以内である必要があります");
    }
}

// AppSettings のバリデーター
public class AppSettingsValidator : AbstractValidator<AppSettings>
{
    public AppSettingsValidator()
    {
        RuleFor(x => x.VoiceVox).SetValidator(new VoiceVoxSettingsValidator());
        RuleFor(x => x.Cache).SetValidator(new CacheSettingsValidator());
        RuleFor(x => x.Audio).SetValidator(new AudioSettingsValidator());
        RuleFor(x => x.Filler).SetValidator(new FillerSettingsValidator());
        RuleFor(x => x.Logging).SetValidator(new LoggingSettingsValidator());
        RuleFor(x => x.Test).SetValidator(new TestSettingsValidator());
    }
}
