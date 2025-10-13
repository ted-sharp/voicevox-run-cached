using NAudio.MediaFoundation;
using Serilog;

namespace VoicevoxRunCached.Services;

public static class MediaFoundationManager
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            MediaFoundationApi.Startup();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeShutdown();
        }
    }

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 0)
        {
            Initialize();
        }
    }

    public static void Shutdown()
    {
        SafeShutdown();
    }

    private static void SafeShutdown()
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 1)
        {
            try
            { MediaFoundationApi.Shutdown(); }
            catch (Exception ex)
            { Log.Debug(ex, "Failed to shutdown MediaFoundation"); }
        }
    }
}


