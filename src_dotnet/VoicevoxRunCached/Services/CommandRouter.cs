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
    private readonly BenchmarkCommandHandler _benchmarkHandler;
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
        BenchmarkCommandHandler benchmarkHandler,
        TextToSpeechProcessor ttsProcessor,
        AppSettings settings,
        ILogger logger)
    {
        _speakerHandler = speakerHandler ?? throw new ArgumentNullException(nameof(speakerHandler));
        _deviceHandler = deviceHandler ?? throw new ArgumentNullException(nameof(deviceHandler));
        _initHandler = initHandler ?? throw new ArgumentNullException(nameof(initHandler));
        _cacheHandler = cacheHandler ?? throw new ArgumentNullException(nameof(cacheHandler));
        _benchmarkHandler = benchmarkHandler ?? throw new ArgumentNullException(nameof(benchmarkHandler));
        _ttsProcessor = ttsProcessor ?? throw new ArgumentNullException(nameof(ttsProcessor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ExecuteCommandAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
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
                "--init" => await _initHandler.HandleInitAsync(cancellationToken),
                "--clear" => await _cacheHandler.HandleClearCacheAsync(),
                "--benchmark" => await _benchmarkHandler.HandleBenchmarkAsync(cancellationToken),
                "--test" => await HandleTestCommandAsync(subArgs, cancellationToken),
                _ => await ExecuteTextToSpeechCommandAsync(args, cancellationToken)
            };
        }
        catch (VoicevoxRunCachedException ex)
        {
            _logger.LogError(ex, "コマンド実行中にエラーが発生しました: {Command}, ErrorCode: {ErrorCode}", command, ex.ErrorCode);
            ConsoleHelper.WriteError($"Error: {ex.UserMessage}", _logger);

            if (!String.IsNullOrEmpty(ex.SuggestedSolution))
            {
                ConsoleHelper.WriteLine($"解決策: {ex.SuggestedSolution}", _logger);
            }

            return ErrorHandlingUtility.GetExitCodeFromErrorCode(ex.ErrorCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("コマンド実行がキャンセルされました: {Command}", command);
            ConsoleHelper.WriteLine("操作がキャンセルされました。", _logger);
            return 0;
        }
        catch (Exception ex)
        {
            return ErrorHandlingUtility.HandleExceptionAndGetExitCode(ex, _logger, $"コマンド実行: {command}", String.Join(" ", subArgs));
        }
    }

    private async Task<int> HandleTestCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var testMessage = _settings.Test.Message;
        if (String.IsNullOrWhiteSpace(testMessage))
        {
            ConsoleHelper.WriteError("Error: Test.Message is empty in configuration", _logger);
            return 1;
        }

        _logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
        ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}", _logger);

        // --test 自体は引数の先頭にテキストを取らないので、Test.Message を直接乗せて
        // 残りのオプション (--speed/--pitch/--out 等) を args そのまま再利用する。
        var request = ArgumentParser.BuildRequestFromOptions(testMessage, args, _settings, _logger);
        var options = ArgumentParser.ParseTextToSpeechOptions(args);
        return await RunTextToSpeechAsync(request, options, cancellationToken);
    }

    private async Task<int> ExecuteTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ArgumentParser.ParseArguments(args, _settings, _logger);
        if (request == null)
        {
            ConsoleHelper.WriteError("Error: Invalid arguments", _logger);
            ArgumentParser.ShowUsage();
            return 1;
        }

        var options = ArgumentParser.ParseTextToSpeechOptions(args);
        return await RunTextToSpeechAsync(request, options, cancellationToken);
    }

    private Task<int> RunTextToSpeechAsync(Models.VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken)
    {
        return _ttsProcessor.ProcessTextToSpeechAsync(
            request, options.NoCache, options.CacheOnly, options.Verbose, options.OutPath, options.NoPlay, cancellationToken);
    }
}
