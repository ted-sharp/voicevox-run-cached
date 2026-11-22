using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// テストごとの <see cref="SpeechLock"/> のロックファイル。Dispose で消す。
/// </summary>
/// <remarks>
/// 本物 (<see cref="InterProcessLock.SpeechLockPath"/>) を使うと、実際の読み上げ (Hooks) やほかのテストとロックを取り合い、
/// 待たされてタイムアウトする (100ms ごとの再試行なので、取り合う相手が多いと取れないまま待ち続ける)。
/// </remarks>
internal sealed class TestLockFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vrc_lock_{Guid.NewGuid():N}.lock");

    public SpeechLock CreateSpeechLock(SpeechEngineProvider engines, LockSettings? settings = null)
        => new(engines, settings ?? new LockSettings(), NullLogger.Instance) { LockFilePath = Path };

    public void Dispose() => File.Delete(Path);
}

/// <summary>
/// テストごとの <see cref="PlaybackLock"/> のロックファイルを置くフォルダ。Dispose で消す (理由は <see cref="TestLockFile"/> と同じ)。
/// </summary>
internal sealed class TempLockDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vrc_locks_{Guid.NewGuid():N}")).FullName;

    public PlaybackLock CreatePlaybackLock(Microsoft.Extensions.Logging.ILogger? logger = null)
        => new(logger ?? NullLogger.Instance) { LockDirectory = Path };

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
