using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services.Infrastructure;
using VoicevoxRunCached.Services.Synthesis;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// VOICEVOX エンジンの代わりに 127.0.0.1 の空きポートで応答するテスト用 HTTP サーバー。
/// /version・/initialize_speaker・/audio_query・/synthesis に応答する。
/// </summary>
internal sealed class FakeEngineServer : IDisposable
{
    // /audio_query が返す JSON の最小形
    private const string AudioQueryJson =
        """{"accent_phrases":[],"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0,"volumeScale":1.0,"outputSamplingRate":24000,"kana":"ア"}""";

    private readonly HttpListener _listener = new();
    private readonly Func<string, bool> _audioQueryFails;
    private readonly Task _serveTask;

    /// <param name="audioQueryFails">true を返したテキストだけ /audio_query を 400 (リトライ対象外) にする</param>
    public FakeEngineServer(Func<string, bool>? audioQueryFails = null)
    {
        _audioQueryFails = audioQueryFails ?? (_ => false);
        BaseUrl = $"http://127.0.0.1:{GetFreePort()}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _serveTask = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>
    /// このサーバーに向けた設定 (エンジンの自動起動はしない)。
    /// </summary>
    public VoiceVoxSettings CreateSettings() => new()
    {
        BaseUrl = BaseUrl,
        DefaultSpeaker = 1,
        AutoStartEngine = false,
    };

    /// <summary>
    /// このサーバーに向けた API クライアント (リトライ待ちは 1ms)。
    /// </summary>
    public VoiceVoxApiClient CreateApiClient() => new(
        CreateSettings(),
        new RetryPolicyService(NullLogger.Instance, baseDelayMs: 1, maxDelayMs: 1),
        NullLogger.Instance);

    public void Dispose()
    {
        _listener.Close(); // 待機中の GetContextAsync が例外で抜け、ServeAsync が終わる
        _serveTask.Wait(TimeSpan.FromSeconds(5));
    }

    private async Task ServeAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            await context.Request.InputStream.CopyToAsync(Stream.Null);
            var (status, body) = Respond(context.Request);
            context.Response.StatusCode = (int)status;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private (HttpStatusCode Status, byte[] Body) Respond(HttpListenerRequest request) => request.Url!.AbsolutePath switch
    {
        "/version" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes("\"0.0.0\"")),
        "/initialize_speaker" => (HttpStatusCode.NoContent, []),
        "/audio_query" when _audioQueryFails(request.QueryString["text"] ?? "") => (HttpStatusCode.BadRequest, []),
        "/audio_query" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(AudioQueryJson)),
        "/synthesis" => (HttpStatusCode.OK, TestAudio.SineWav(24000, 200)),
        _ => (HttpStatusCode.NotFound, []),
    };

    /// <summary>
    /// 空いているポート番号。待ち受けていない (= エンジンが止まっている) URL を作るのにも使う。
    /// </summary>
    public static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
