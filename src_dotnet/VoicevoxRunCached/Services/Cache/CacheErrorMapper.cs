using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// キャッシュ I/O 系の例外を <see cref="CacheException"/> に変換する共通ロジック。
/// AudioCacheManager と CacheEvictionPolicy で同型の catch を散らさないために集約している。
/// </summary>
internal static class CacheErrorMapper
{
    public enum OperationKind
    {
        Write,
        Clear,
        Cleanup,
    }

    public static async Task<T> WithMappingAsync<T>(
        OperationKind kind,
        string operation,
        string? cacheKey,
        string cacheDirectory,
        ILogger logger,
        Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CacheException)
        {
            throw;
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Map(logger, ex, ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied during {operation}",
                $"{operation}のアクセス権限がありません。",
                "キャッシュディレクトリのアクセス権限を確認してください。",
                cacheKey, cacheDirectory);
        }
        catch (IOException ex) when (ex.Message.Contains("There is not enough space"))
        {
            throw Map(logger, ex, ErrorCodes.Cache.CacheFull,
                $"Cache directory is full during {operation}",
                "キャッシュディレクトリの容量が不足しています。",
                "古いキャッシュファイルを削除するか、キャッシュディレクトリの容量を増やしてください。",
                cacheKey, cacheDirectory);
        }
        catch (IOException ex)
        {
            var code = kind == OperationKind.Write
                ? ErrorCodes.Cache.CacheWriteError
                : ErrorCodes.Cache.CacheReadError;
            throw Map(logger, ex, code,
                $"I/O error during {operation}: {ex.Message}",
                $"{operation}のファイル操作に失敗しました。",
                "ディスクの空き容量とキャッシュディレクトリの状態を確認してください。",
                cacheKey, cacheDirectory);
        }
        catch (Exception ex)
        {
            throw Map(logger, ex, ErrorCodes.General.UnknownError,
                $"Unexpected error during {operation}: {ex.Message}",
                $"{operation}中に予期しないエラーが発生しました。",
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。",
                cacheKey, cacheDirectory);
        }
    }

    private static CacheException Map(
        ILogger logger,
        Exception inner,
        string errorCode,
        string message,
        string userMessage,
        string solution,
        string? cacheKey,
        string cacheDirectory)
    {
        logger.LogError(inner, "{Message}: {CacheKey}", message, cacheKey ?? "-");
        return new CacheException(errorCode, message, userMessage, inner, cacheKey, cacheDirectory, solution);
    }
}
