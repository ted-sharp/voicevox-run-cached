using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// <see cref="CommandRouter"/> のコマンドの振り分けと、コマンドごとの引数の検証。エンジンには接続しない。
/// </summary>
public class CommandRouterTests
{
    private static async Task WithRouterAsync(Func<CommandRouter, string, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vrc_router_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            // 振り分けを誤ってエンジンに接続しても、手元で動いているエンジンには届かないようにする
            VoiceVox = new VoiceVoxSettings { BaseUrl = $"http://127.0.0.1:{FakeEngineServer.GetFreePort()}", AutoStartEngine = false },
            Audio = new AudioSettings { PrepareDevice = false },
            Cache = new CacheSettings { Directory = Path.Combine(dir, "audio"), UseExecutableBaseDirectory = false },
            Filler = new FillerSettings { Directory = Path.Combine(dir, "filler"), UseExecutableBaseDirectory = false },
        };
        try
        {
            using var provider = new AppServiceProvider { Settings = settings, Logger = NullLogger.Instance };
            await test(provider.GetService<CommandRouter>(), settings.Cache.Directory);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Test]
    public async Task Clear_WithUnexpectedArgument_DoesNotDeleteCache()
    {
        // 回帰: --clear は引数を見ておらず、--clear --speaker 3 (話者 3 の分だけ消すつもり) でも全部消して成功していた
        await WithRouterAsync(async (router, cacheDir) =>
        {
            var cached = Path.Combine(cacheDir, "key.opus");
            await File.WriteAllBytesAsync(cached, [1]);

            var exitCode = await router.ExecuteCommandAsync(["--clear", "--speaker", "3"]);

            await Assert.That(exitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.General.InvalidArguments));
            await Assert.That(File.Exists(cached)).IsTrue();
        });
    }

    [Test]
    public async Task CommandName_IsCaseSensitive()
    {
        // コマンド名はオプションと同じく大文字小文字を区別する。"Speakers" は話者一覧ではなく本文として読む
        // (--cache-only なのでエンジンに接続せず、キャッシュが無いことで本文として扱われたと分かる)
        await WithRouterAsync(async (router, _) =>
        {
            var exitCode = await router.ExecuteCommandAsync(["Speakers", "--cache-only", "--no-play"]);

            await Assert.That(exitCode).IsEqualTo(ErrorCodes.GetExitCode(ErrorCodes.Cache.CacheReadError));
        });
    }
}
