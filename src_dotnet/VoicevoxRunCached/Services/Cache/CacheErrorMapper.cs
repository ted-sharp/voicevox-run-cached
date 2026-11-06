using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// キャッシュ I/O 系の例外を <see cref="CacheException"/> に変換する共通ロジック。
/// 現状は CacheEvictionPolicy / AudioCacheManager.ClearAllCacheAsync / FillerManager.ClearFillerCacheAsync (--clear) など、ユーザーに直接 surface する
/// 読み取り側の cleanup 経路でのみ使用する (Write 経路は best-effort で握り潰すので mapping 不要)。
/// </summary>
internal static class CacheErrorMapper
{
    public static async Task<T> WithMappingAsync<T>(
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
                $"Access denied during {operation}: {ex.Message}",
                cacheKey, cacheDirectory);
        }
        catch (IOException ex)
        {
            throw Map(logger, ex, ErrorCodes.Cache.CacheReadError,
                $"I/O error during {operation}: {ex.Message}",
                cacheKey, cacheDirectory);
        }
        catch (Exception ex)
        {
            throw Map(logger, ex, ErrorCodes.General.UnknownError,
                $"Unexpected error during {operation}: {ex.Message}",
                cacheKey, cacheDirectory);
        }
    }

    private static CacheException Map(
        ILogger logger,
        Exception inner,
        string errorCode,
        string message,
        string? cacheKey,
        string cacheDirectory)
    {
        logger.LogError(inner, "{Message} (Key: {CacheKey}, Dir: {CacheDirectory})",
            message, cacheKey ?? "-", cacheDirectory);
        return new CacheException(errorCode, message, message, inner);
    }
}
