using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services.Commands;

namespace VoicevoxRunCached.Services;

/// <summary>
/// コマンドのルーティングと実行を行うクラス
/// </summary>
public class CommandRouter
{
    private readonly CacheCommandHandler _cacheHandler;
    private readonly DeviceCommandHandler _deviceHandler;
    private readonly InitCommandHandler _initHandler;
    private readonly SpeakerCommandHandler _speakerHandler;
    private readonly TextToSpeechProcessor _ttsProcessor;
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    public CommandRouter(
        SpeakerCommandHandler speakerHandler,
        DeviceCommandHandler deviceHandler,
        InitCommandHandler initHandler,
        CacheCommandHandler cacheHandler,
        TextToSpeechProcessor ttsProcessor,
        AppSettings settings,
        ILogger logger)
    {
        _speakerHandler = speakerHandler ?? throw new ArgumentNullException(nameof(speakerHandler));
        _deviceHandler = deviceHandler ?? throw new ArgumentNullException(nameof(deviceHandler));
        _initHandler = initHandler ?? throw new ArgumentNullException(nameof(initHandler));
        _cacheHandler = cacheHandler ?? throw new ArgumentNullException(nameof(cacheHandler));
        _ttsProcessor = ttsProcessor ?? throw new ArgumentNullException(nameof(ttsProcessor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ExecuteCommandAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || ArgumentParser.IsHelpRequested(args))
        {
            ArgumentParser.ShowUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "speakers" => await _speakerHandler.HandleSpeakersAsync(cancellationToken),
                "devices" => _deviceHandler.HandleDevices(subArgs),
                "--init" => await _initHandler.HandleInitAsync(ArgumentParser.ParseInitSpeaker(subArgs, _settings), cancellationToken),
                "--clear" => await _cacheHandler.HandleClearCacheAsync(cancellationToken),
                "--test" => await HandleTestCommandAsync(subArgs, cancellationToken),
                // "--" (ArgumentParser.TextSeparator) もここに来る。続く引数はコマンド名と同じでも本文として読む
                _ => await ExecuteTextToSpeechCommandAsync(args, cancellationToken)
            };
        }
        catch (VoicevoxRunCachedException ex)
        {
            // 利用者の入力ミスはスタックトレースが不要なので Warning で要点だけ残す
            if (ex.ErrorCode == ErrorCodes.General.InvalidArguments)
            {
                _logger.LogWarning("入力が不正です: {Command}, {Message}", command, ex.Message);
            }
            else if (ex.ErrorCode == ErrorCodes.Lock.LockWaitTimeout)
            {
                // 待つ上限を指定したときの想定内の結果なので、スタックトレースは残さない
                _logger.LogWarning("ロックを待ちきれなかったため実行しませんでした: {Command}, {Message}", command, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "コマンド実行中にエラーが発生しました: {Command}, ErrorCode: {ErrorCode}", command, ex.ErrorCode);
            }
            ConsoleHelper.WriteError($"Error: {ex.UserMessage}");

            if (!String.IsNullOrEmpty(ex.SuggestedSolution))
            {
                ConsoleHelper.WriteErrorDetail($"解決策: {ex.SuggestedSolution}");
            }

            return ErrorCodes.GetExitCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("コマンド実行がキャンセルされました: {Command}", command);
            ConsoleHelper.WriteWarning("操作がキャンセルされました。");
            return ErrorCodes.CancelledExitCode;
        }
        catch (Exception ex)
        {
            return ErrorHandlingUtility.HandleExceptionAndGetExitCode(ex, _logger, $"コマンド実行: {command}", String.Join(" ", subArgs));
        }
    }

    private async Task<int> HandleTestCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        // Test セクションの検証は AppSettingsValidator から外しているので、ここで実行する
        // (--test を使わないユーザーには Test.Message の必須チェックを走らせない)
        var testValidation = new TestSettingsValidator().Validate(_settings.Test);
        if (!testValidation.IsValid)
        {
            var errors = String.Join("; ", testValidation.Errors.Select(e => e.ErrorMessage));
            throw new ConfigurationException(
                errorCode: ErrorCodes.Configuration.InvalidSettings,
                message: $"Test settings invalid: {errors}",
                userMessage: $"Test 設定が無効です: {errors}",
                suggestedSolution: "appsettings.json の Test.Message を確認してください。");
        }

        var testMessage = _settings.Test.Message;
        _logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
        ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}");

        // --test 自体は引数の先頭にテキストを取らないので、Test.Message を直接乗せて
        // 残りのオプション (--speed/--pitch/--out 等) を args そのまま再利用する。
        var (request, options) = ArgumentParser.ParseTestCommand(testMessage, args, _settings);
        return await _ttsProcessor.ProcessTextToSpeechAsync(request, options, cancellationToken);
    }

    private async Task<int> ExecuteTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var (request, options) = ArgumentParser.Parse(args, _settings, _logger);
        if (request == null)
        {
            ConsoleHelper.WriteError("Error: Invalid arguments");
            ArgumentParser.ShowUsage();
            return 1;
        }

        return await _ttsProcessor.ProcessTextToSpeechAsync(request, options, cancellationToken);
    }
}
