using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

public class ConfigurationValidationService
{
    private readonly AppSettingsValidator _validator = new();

    public void ValidateConfiguration(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var result = _validator.Validate(settings);
        if (!result.IsValid)
        {
            var errors = String.Join(Environment.NewLine, result.Errors.Select(e => $"- {e.PropertyName}: {e.ErrorMessage}"));
            // 検証は Serilog 構成前に走るので、Message はどこにも出力されない。
            // どの項目が不正か利用者に分かるよう、UserMessage にも項目ごとのエラーを載せる
            throw new ConfigurationException(
                ErrorCodes.Configuration.InvalidSettings,
                $"Configuration validation failed: {errors}",
                $"設定の検証に失敗しました。{Environment.NewLine}{errors}",
                suggestedSolution: "appsettings.json を確認してエラー箇所を修正してください。");
        }
    }
}
