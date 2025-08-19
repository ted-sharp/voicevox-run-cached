using FluentValidation;
using VoicevoxRunCached.Configuration;

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
            .Must((settings, path) => string.IsNullOrEmpty(path) || File.Exists(path))
            .WithMessage("EnginePathが指定されている場合、ファイルが存在する必要があります");
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
            .NotEmpty().WithMessage("Cache Directoryは必須です");

        RuleFor(x => x.ExpirationDays)
            .InclusiveBetween(1, 365).WithMessage("ExpirationDaysは1-365日の範囲である必要があります");

        RuleFor(x => x.MaxSizeGB)
            .InclusiveBetween(0.1, 100.0).WithMessage("MaxSizeGBは0.1-100.0GBの範囲である必要があります");
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
            .InclusiveBetween(0.0, 5.0).WithMessage("Volumeは0.0-5.0の範囲である必要があります");

        RuleFor(x => x.PreparationDurationMs)
            .InclusiveBetween(50, 2000).WithMessage("PreparationDurationMsは50-2000ミリ秒の範囲である必要があります");

        RuleFor(x => x.PreparationVolume)
            .InclusiveBetween(0.001, 0.1).WithMessage("PreparationVolumeは0.001-0.1の範囲である必要があります");
    }
}

// FillerSettings のバリデーター
public class FillerSettingsValidator : AbstractValidator<FillerSettings>
{
    public FillerSettingsValidator()
    {
        RuleFor(x => x.Directory)
            .NotEmpty().WithMessage("Filler Directoryは必須です");

        RuleFor(x => x.FillerTexts)
            .NotEmpty().WithMessage("FillerTextsは空であってはいけません")
            .Must(texts => texts.All(t => !string.IsNullOrWhiteSpace(t)))
            .WithMessage("FillerTextsの各要素は空文字列であってはいけません");
    }
}

// LoggingSettings のバリデーター
public class LoggingSettingsValidator : AbstractValidator<LoggingSettings>
{
    public LoggingSettingsValidator()
    {
        RuleFor(x => x.Level)
            .IsInEnum().WithMessage("Levelは有効なログレベルである必要があります");

        RuleFor(x => x.Format)
            .Must(format => format == "simple" || format == "json")
            .WithMessage("Formatは'simple'または'json'である必要があります");
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
