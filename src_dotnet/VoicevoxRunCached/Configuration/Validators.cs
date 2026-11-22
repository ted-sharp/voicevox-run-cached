using FluentValidation;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Configuration;

// SynthesisSettings のバリデーター
public class SynthesisSettingsValidator : AbstractValidator<SynthesisSettings>
{
    public SynthesisSettingsValidator()
    {
        // 名前でない値はバインドで失敗する。数字 ("Engine": 5) は未定義の値として入るのでここで弾く
        RuleFor(x => x.Engine)
            .IsInEnum().WithMessage($"Engineは {String.Join(" / ", Enum.GetNames<SpeechEngineKind>())} のどれかである必要があります");
    }
}

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
            .Must((_, path) => String.IsNullOrEmpty(path) || File.Exists(path))
            .WithMessage("EnginePathが指定されている場合、ファイルが存在する必要があります");

        Include(new EngineFillerSettingsValidator());
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
            .Must(DirectoryValidation.BeWritableDirectory).WithMessage("Cache Directoryは書き込み可能である必要があります");

        RuleFor(x => x.ExpirationDays)
            .InclusiveBetween(1, 3650).WithMessage("ExpirationDaysは1-3650日（10年）の範囲である必要があります");

        RuleFor(x => x.MaxSizeGb)
            .InclusiveBetween(0.1, 1000.0).WithMessage("MaxSizeGbは0.1-1000.0GBの範囲である必要があります");

        RuleFor(x => x.OpusBitrateKbps)
            .InclusiveBetween(AudioConstants.MinOpusBitrateKbps, AudioConstants.MaxOpusBitrateKbps)
            .WithMessage($"OpusBitrateKbpsは{AudioConstants.MinOpusBitrateKbps}-{AudioConstants.MaxOpusBitrateKbps}kbpsの範囲である必要があります");
    }
}

internal static class DirectoryValidation
{
    public static bool BeWritableDirectory(string path)
    {
        try
        {
            if (String.IsNullOrWhiteSpace(path))
                return false;

            // 必要であれば作成する (初回起動時はキャッシュディレクトリが存在しない)
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            // 検証はプロセス間ロックより前 (起動直後) に走るので、Hooks で同時に起動したプロセス同士が
            // 同じファイルを書いて共有違反にならないよう、毎回別の名前にする。
            // 名前が毎回違うので、File.Delete の失敗 (ウイルス対策ソフトが一時的に掴む等) で残ると溜まっていく。
            // DeleteOnClose で閉じたときに OS に消させる
            var testPath = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }
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
        RuleForEach(x => x.Devices).Custom((device, context) =>
        {
            if (!OutputDeviceSelector.TryValidateSpec(device, out var error))
            {
                context.AddFailure($"Devicesの指定が不正です: {error}");
            }
        });

        RuleFor(x => x.Volume)
            .InclusiveBetween(0.0, 1.0).WithMessage("Volumeは0.0-1.0の範囲である必要があります");

        RuleFor(x => x.PreparationDurationMs)
            .InclusiveBetween(50, 5000).WithMessage("PreparationDurationMsは50-5000ミリ秒の範囲である必要があります");

        RuleFor(x => x.PreparationVolume)
            .InclusiveBetween(0.0, 1.0).WithMessage("PreparationVolumeは0.0-1.0の範囲である必要があります");

        // 新しい検証ルール
        RuleFor(x => x.DesiredLatency)
            .InclusiveBetween(10, 1000).WithMessage("DesiredLatencyは10-1000ミリ秒の範囲である必要があります");

        RuleFor(x => x.NumberOfBuffers)
            .InclusiveBetween(1, 10).WithMessage("NumberOfBuffersは1-10の範囲である必要があります");

        RuleForEach(x => x.SpeakerDevices).SetValidator(new SpeakerDeviceSettingsValidator());

        RuleForEach(x => x.ExcludedDevices).Custom((device, context) =>
        {
            if (!OutputDeviceSelector.TryValidateExcludedSpec(device, out var error))
            {
                context.AddFailure($"ExcludedDevicesの指定が不正です: {error}");
            }
        });
    }
}

// Audio.SpeakerDevices の 1 項目のバリデーター
public class SpeakerDeviceSettingsValidator : AbstractValidator<SpeakerDeviceSettings>
{
    public SpeakerDeviceSettingsValidator()
    {
        RuleFor(x => x.Engine)
            .IsInEnum().WithMessage($"SpeakerDevicesのEngineは {String.Join(" / ", Enum.GetNames<SpeechEngineKind>())} のどれかである必要があります");

        // 書き間違いで黙って Audio.Devices に鳴らさないよう、--speaker と同じ検証をする
        RuleFor(x => x.Speaker).Custom((speaker, context) =>
        {
            var entry = context.InstanceToValidate;
            if (Enum.IsDefined(entry.Engine) && SpeechEngineProvider.NormalizeSpeaker(entry.Engine, speaker ?? "", out var error) == null)
            {
                context.AddFailure($"SpeakerDevicesのSpeakerの指定が不正です: {error}");
            }
        });

        // 空だと話者を書いた意味が無い (Device と単数で書き間違えたときもここに来る)
        RuleFor(x => x.Devices)
            .NotEmpty().WithMessage("SpeakerDevicesのDevicesにデバイスを1つ以上書いてください");

        RuleForEach(x => x.Devices).Custom((device, context) =>
        {
            if (!OutputDeviceSelector.TryValidateSpec(device, out var error))
            {
                context.AddFailure($"SpeakerDevicesのDevicesの指定が不正です: {error}");
            }
        });
    }
}

// FillerSettings のバリデーター
public class FillerSettingsValidator : AbstractValidator<FillerSettings>
{
    public FillerSettingsValidator()
    {
        RuleFor(x => x.Directory)
            .NotEmpty().When(x => x.Enabled).WithMessage("Filler Directoryは必須です（フィラーが有効な場合）")
            .Must(DirectoryValidation.BeWritableDirectory).When(x => x.Enabled).WithMessage("Filler Directoryは書き込み可能である必要があります");

        RuleFor(x => x.FillerTexts)
            .NotEmpty().When(x => x.Enabled).WithMessage("FillerTextsは空であってはいけません（フィラーが有効な場合）")
            .Must(texts => texts?.All(t => !String.IsNullOrWhiteSpace(t)) ?? true)
            .When(x => x.Enabled).WithMessage("FillerTextsの各要素は空文字列であってはいけません");
    }
}

// LockSettings のバリデーター
public class LockSettingsValidator : AbstractValidator<LockSettings>
{
    public LockSettingsValidator()
    {
        RuleFor(x => x.MaxWaitSeconds)
            .GreaterThanOrEqualTo(0).When(x => x.MaxWaitSeconds.HasValue)
            .WithMessage("MaxWaitSecondsは0以上である必要があります (未設定なら上限なし)");
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
        RuleFor(x => x.MaxFileSizeMb)
            .InclusiveBetween(1, 1000).When(x => x.EnableFileLogging).WithMessage("MaxFileSizeMbは1-1000MBの範囲である必要があります");

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

// TestSettings のバリデーター。--test 実行時のみ CommandRouter から手動で適用する
// (起動時の AppSettingsValidator から外しているのは、--test を使わないユーザーに不要な検証を強要しないため)
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
        RuleFor(x => x.Synthesis).SetValidator(new SynthesisSettingsValidator());
        RuleFor(x => x.VoiceVox).SetValidator(new VoiceVoxSettingsValidator());
        RuleFor(x => x.Cache).SetValidator(new CacheSettingsValidator());
        RuleFor(x => x.Audio).SetValidator(new AudioSettingsValidator());
        RuleFor(x => x.Filler).SetValidator(new FillerSettingsValidator());
        RuleFor(x => x.Lock).SetValidator(new LockSettingsValidator());
        RuleFor(x => x.Logging).SetValidator(new LoggingSettingsValidator());
        RuleFor(x => x.Voicepeak).SetValidator(new VoicepeakSettingsValidator());
        RuleFor(x => x.Cevio).SetValidator(new CevioSettingsValidator());

        // 同じディレクトリだと、キャッシュの容量超過の削除がフィラーの .opus も数えて消し、--clear もまとめて消す。
        // --init は Filler.Enabled に関係なく生成するので、Enabled でなくても検証する
        RuleFor(x => x)
            .Must(x => !IsSameDirectory(x.Cache.Directory, x.Filler.Directory))
            .WithName("Filler.Directory")
            .WithMessage("Cache Directory と Filler Directory には別のディレクトリを指定する必要があります");
    }

    /// <remarks>
    /// <c>UseExecutableBaseDirectory</c> が true の相対パスは、検証前に <c>ApplicationBootstrap.BuildAppSettings</c> で
    /// 実行ファイル基準の絶対パスにしてある。false の相対パスは実行時と同じくカレントディレクトリ基準で <c>GetFullPath</c> して比べる。
    /// 末尾の区切り文字の有無と大文字小文字 (Windows のファイルシステムは区別しない) は無視する。
    /// </remarks>
    private static bool IsSameDirectory(string first, string second)
    {
        if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second))
        {
            return false;
        }
        return String.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    }
}

// VoicepeakSettings のバリデーター
public class VoicepeakSettingsValidator : AbstractValidator<VoicepeakSettings>
{
    public VoicepeakSettingsValidator()
    {
        RuleFor(x => x.ExePath)
            .Must(path => String.IsNullOrEmpty(path) || File.Exists(path))
            .WithMessage("ExePathが指定されている場合、ファイルが存在する必要があります");

        // 空なら --engine voicepeak のときに --speaker が必須 (ArgumentParser が案内する)
        RuleFor(x => x.DefaultSpeaker).Custom((speaker, context) =>
        {
            if (!String.IsNullOrEmpty(speaker) && VoicepeakEngine.NormalizeSpeaker(speaker, out var error) == null)
            {
                context.AddFailure($"DefaultSpeakerの指定が不正です: {error}");
            }
        });

        RuleFor(x => x.TimeoutSeconds)
            .InclusiveBetween(10, 600).WithMessage("TimeoutSecondsは10-600秒の範囲である必要があります");

        Include(new EngineFillerSettingsValidator());
    }
}

// CevioSettings のバリデーター
public class CevioSettingsValidator : AbstractValidator<CevioSettings>
{
    public CevioSettingsValidator()
    {
        // 空なら --engine cevio のときに --speaker が必須 (ArgumentParser が案内する)
        RuleFor(x => x.DefaultSpeaker).Custom((speaker, context) =>
        {
            if (!String.IsNullOrEmpty(speaker) && CevioEngine.NormalizeSpeaker(speaker, out var error) == null)
            {
                context.AddFailure($"DefaultSpeakerの指定が不正です: {error}");
            }
        });

        RuleFor(x => x.StartupTimeoutSeconds)
            .InclusiveBetween(5, 600).WithMessage("StartupTimeoutSecondsは5-600秒の範囲である必要があります");

        RuleFor(x => x.HostPath)
            .Must(path => String.IsNullOrEmpty(path) || File.Exists(path))
            .WithMessage("HostPathが指定されている場合、ファイルが存在する必要があります");

        RuleFor(x => x.SynthesisTimeoutSeconds)
            .InclusiveBetween(1, 300).WithMessage("SynthesisTimeoutSecondsは1-300秒の範囲である必要があります");

        Include(new EngineFillerSettingsValidator());
    }
}

// 各エンジンのセクションのフィラー設定 (IEngineFillerSettings) のバリデーター
public class EngineFillerSettingsValidator : AbstractValidator<IEngineFillerSettings>
{
    public EngineFillerSettingsValidator()
    {
        RuleFor(x => x.FillerTexts)
            .Must(texts => texts.All(t => !String.IsNullOrWhiteSpace(t)))
            .WithMessage("FillerTextsの各要素は空文字列であってはいけません");

        RuleFor(x => x.MaxFillersPerWait)
            .GreaterThanOrEqualTo(0).When(x => x.MaxFillersPerWait.HasValue)
            .WithMessage("MaxFillersPerWaitは0以上である必要があります (null なら上限なし)");
    }
}
