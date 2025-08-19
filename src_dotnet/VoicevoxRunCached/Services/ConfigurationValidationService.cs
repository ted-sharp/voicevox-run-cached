using FluentValidation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Configuration.Validators;

namespace VoicevoxRunCached.Services;

public class ConfigurationValidationService
{
    private readonly AppSettingsValidator _validator;

    public ConfigurationValidationService()
    {
        this._validator = new AppSettingsValidator();
    }

    public async Task<FluentValidation.Results.ValidationResult> ValidateConfigurationAsync(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return await this._validator.ValidateAsync(settings);
    }

    public void ValidateConfiguration(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var result = this._validator.Validate(settings);
        if (!result.IsValid)
        {
            var errors = String.Join(Environment.NewLine, result.Errors.Select(e => $"- {e.PropertyName}: {e.ErrorMessage}"));
            throw new InvalidOperationException($"設定の検証に失敗しました:{Environment.NewLine}{errors}");
        }
    }

    public async Task<bool> IsConfigurationValidAsync(AppSettings settings)
    {
        try
        {
            var result = await this.ValidateConfigurationAsync(settings);
            return result.IsValid;
        }
        catch
        {
            return false;
        }
    }

    public List<string> GetValidationErrors(AppSettings settings)
    {
        if (settings == null)
        {
            return ["設定がnullです"];
        }

        var result = this._validator.Validate(settings);
        return result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}").ToList();
    }
}
