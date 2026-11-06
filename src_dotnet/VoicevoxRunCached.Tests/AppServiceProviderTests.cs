using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// Jab 生成プロバイダの結線 (解決・singleton・終了時の Dispose) の回帰検知。
/// 登録漏れ自体はビルドエラーになるため、ここでは実行時の振る舞いだけを見る。
/// </summary>
public class AppServiceProviderTests
{
    private static (AppServiceProvider provider, string dir) CreateProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_di_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            // デバイス warm-up (無音再生) を走らせない
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = Path.Combine(dir, "audio"), UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };
        return (new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance }, dir);
    }

    private static void DeleteDirectory(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task CommandRouter_CanBeResolved()
    {
        var (provider, dir) = CreateProvider();
        try
        {
            using (provider)
            {
                var router = provider.GetService<CommandRouter>();

                await Assert.That(router).IsNotNull();
            }
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Services_AreSingletons()
    {
        var (provider, dir) = CreateProvider();
        try
        {
            using (provider)
            {
                var first = provider.GetService<AudioPlayer>();
                var second = provider.GetService<AudioPlayer>();

                await Assert.That(second).IsSameReferenceAs(first);
            }
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Test]
    public async Task Dispose_DisposesSingletons()
    {
        var (provider, dir) = CreateProvider();
        try
        {
            var player = provider.GetService<AudioPlayer>();

            provider.Dispose();

            var reader = Channel.CreateUnbounded<TextSegment>().Reader;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => player.PlayStreamingAsync(reader));
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }
}
