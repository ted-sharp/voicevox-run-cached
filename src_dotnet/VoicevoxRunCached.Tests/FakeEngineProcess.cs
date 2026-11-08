using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// エンジンの代わりに自動起動させるプロセス (cmd.exe 経由で ping を約 30 秒動かす)。
/// 動いている間は出力のリダイレクト先 (<see cref="MarkerPath"/>) を開いたままにするので、
/// 止まったかどうかをそのファイルを排他で開けるかで確かめられる。
/// </summary>
/// <remarks>
/// /version は <see cref="FakeEngineServer"/> が答える。<c>versionAvailable: () =&gt; process.HasStarted</c> にすると、
/// 最初の死活確認が失敗してこのプロセスを起動し、起動したところで起動済みになる (= 自分が起動したエンジンになる)。
/// </remarks>
internal sealed class FakeEngineProcess : IDisposable
{
    public string MarkerPath { get; } = Path.Combine(Path.GetTempPath(), $"vrc_engine_{Guid.NewGuid():N}.txt");

    /// <summary>
    /// 起動した (<see cref="MarkerPath"/> が作られた) か。止まった後も true のまま。
    /// </summary>
    /// <remarks>
    /// 起動直後に止めると cmd.exe がリダイレクト先を作る前に終わることがあり、止まったかを確かめられなくなるので、
    /// /version はこれが true になってから成功させる (実際のエンジンも起動が終わるまで /version に応答しない)。
    /// </remarks>
    public bool HasStarted => File.Exists(MarkerPath);

    /// <summary>
    /// <paramref name="baseUrl"/> に向け、このプロセスを自動起動し、終了時に止める (<c>KeepEngineRunning: false</c>) 設定。
    /// </summary>
    public VoiceVoxSettings CreateSettings(string baseUrl) => new()
    {
        BaseUrl = baseUrl,
        DefaultSpeaker = FakeEngineServer.SpeakerId,
        AutoStartEngine = true,
        KeepEngineRunning = false,
        EnginePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        EngineArguments = $"/c ping -n 30 127.0.0.1 > \"{MarkerPath}\"",
        StartupTimeoutSeconds = 10,
    };

    /// <summary>
    /// 時間内にプロセスが止まり、<see cref="MarkerPath"/> が閉じられれば true。
    /// 起動されていない (ファイルが無い) ときも、止まらないときも false。
    /// </summary>
    /// <remarks>
    /// 止める側 (<c>Kill(entireProcessTree: true)</c>) は cmd.exe の終了しか待たないので、子の ping がハンドルを閉じるまで少し待つ。
    /// </remarks>
    public async Task<bool> HasStoppedWithinAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var stream = new FileStream(MarkerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (IOException)
        {
            // テストが失敗してプロセスが残っている (ping は約 30 秒で終わる)
        }
    }
}
