using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// コマンドのルーティングと実行を行うクラス
/// </summary>
public class CommandRouter
{
    private readonly CommandHandler _commandHandler;
    private readonly ILogger _logger;
    private readonly AppSettings _settings;

    public CommandRouter(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _commandHandler = new CommandHandler(settings, logger);
    }

    /// <summary>
    /// コマンドを実行します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>実行結果の終了コード</returns>
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
                "speakers" => await _commandHandler.HandleSpeakersAsync(),
                "devices" => _commandHandler.HandleDevices(subArgs),
                "--init" => await _commandHandler.HandleInitAsync(),
                "--clear" => await _commandHandler.HandleClearCacheAsync(),
                "--benchmark" => await _commandHandler.HandleBenchmarkAsync(),
                "--test" => await HandleTestCommandAsync(subArgs, cancellationToken),
                _ => await HandleTextToSpeechCommandAsync(args, cancellationToken)
            };
        }
        catch (VoicevoxRunCachedException ex)
        {
            // カスタム例外は適切に処理
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
        catch (ArgumentException ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            _logger.LogError(ex, "コマンドの引数が無効です: {Command}, Args: {Args}, ErrorCode: {ErrorCode}", command, String.Join(" ", subArgs), errorCode);
            ConsoleHelper.WriteError($"Error: {userMessage} - {ex.Message}", _logger);
            ConsoleHelper.WriteLine($"解決策: {solution}", _logger);
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
        catch (InvalidOperationException ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            _logger.LogError(ex, "コマンドの実行に必要な前提条件が満たされていません: {Command}, ErrorCode: {ErrorCode}", command, errorCode);
            ConsoleHelper.WriteError($"Error: {userMessage} - {ex.Message}", _logger);
            ConsoleHelper.WriteLine($"解決策: {solution}", _logger);
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
        catch (UnauthorizedAccessException ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            _logger.LogError(ex, "コマンド実行に必要な権限がありません: {Command}, ErrorCode: {ErrorCode}", command, errorCode);
            ConsoleHelper.WriteError($"Error: {userMessage} - {ex.Message}", _logger);
            ConsoleHelper.WriteLine($"解決策: {solution}", _logger);
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
        catch (IOException ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            _logger.LogError(ex, "ファイルまたはディレクトリの操作に失敗しました: {Command}, ErrorCode: {ErrorCode}", command, errorCode);
            ConsoleHelper.WriteError($"Error: {userMessage} - {ex.Message}", _logger);
            ConsoleHelper.WriteLine($"解決策: {solution}", _logger);
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
        catch (Exception ex)
        {
            var userMessage = ErrorHandlingUtility.GetUserFriendlyMessageFromException(ex);
            var solution = ErrorHandlingUtility.GetSuggestedSolutionFromException(ex);
            var errorCode = ErrorHandlingUtility.GetErrorCodeFromException(ex);
            _logger.LogError(ex, "コマンド実行中に予期しないエラーが発生しました: {Command}, ErrorCode: {ErrorCode}", command, errorCode);
            ConsoleHelper.WriteError($"Error: {userMessage} - {ex.Message}", _logger);
            ConsoleHelper.WriteLine($"解決策: {solution}", _logger);
            return ErrorHandlingUtility.GetExitCodeFromErrorCode(errorCode);
        }
    }

    /// <summary>
    /// テストコマンドを処理します
    /// </summary>
    private async Task<int> HandleTestCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var testMessage = _settings.Test.Message;
        if (String.IsNullOrWhiteSpace(testMessage))
        {
            Console.WriteLine("\e[31mError: Test.Message is empty in configuration\e[0m");
            return 1;
        }

        _logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
        ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}", _logger);

        // 設定されたメッセージで最初の引数を置き換え
        var testArgs = new[] { testMessage }.Concat(args).ToArray();

        _logger.LogInformation("Test args constructed: {TestArgs}", String.Join(" ", testArgs));
        _logger.LogDebug("テストコマンド引数の詳細: {TestArgs}", String.Join(" ", testArgs));

        return await ExecuteTextToSpeechCommandAsync(testArgs, cancellationToken);
    }

    /// <summary>
    /// テキスト読み上げコマンドを処理します
    /// </summary>
    private async Task<int> HandleTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        return await ExecuteTextToSpeechCommandAsync(args, cancellationToken);
    }

    /// <summary>
    /// テキスト読み上げコマンドを実行します
    /// </summary>
    private async Task<int> ExecuteTextToSpeechCommandAsync(string[] args, CancellationToken cancellationToken)
    {
        var request = ArgumentParser.ParseArguments(args, _settings, _logger);
        if (request == null)
        {
            ConsoleHelper.WriteError("Error: Invalid arguments", _logger);
            ArgumentParser.ShowUsage();
            return 1;
        }

        var noCache = args.Contains("--no-cache");
        var cacheOnly = args.Contains("--cache-only");
        var verbose = args.Contains("--verbose");
        var outPath = args.FirstOrDefault(arg => arg.StartsWith("--out="))?.Substring("--out=".Length);
        var noPlay = args.Contains("--no-play");

        return await _commandHandler.HandleTextToSpeechAsync(
            request, noCache, cacheOnly, verbose, outPath, noPlay, cancellationToken);
    }
}
