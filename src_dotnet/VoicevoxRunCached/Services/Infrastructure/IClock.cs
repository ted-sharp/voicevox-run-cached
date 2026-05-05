namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 時刻取得を抽象化するためのインターフェース。テストで時刻を mock するために使用する。
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// 本番ランタイム用の <see cref="IClock"/> 実装。<see cref="DateTime.UtcNow"/> を素直に返す。
/// </summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}
