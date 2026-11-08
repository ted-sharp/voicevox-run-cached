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
/// /version・/speakers・/initialize_speaker・/audio_query・/synthesis に応答する。
/// </summary>
internal sealed class FakeEngineServer : IDisposable
{
    /// <summary>/speakers に載せる話者 (スタイル) の ID。</summary>
    public const int SpeakerId = 1;

    // /audio_query が返す JSON の最小形
    private const string AudioQueryJson =
        """{"accent_phrases":[],"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0,"volumeScale":1.0,"outputSamplingRate":24000,"kana":"ア"}""";

    private const string SpeakersJson =
        """[{"name":"テスト話者","speaker_uuid":"00000000-0000-0000-0000-000000000000","styles":[{"name":"ノーマル","id":1}],"version":"0.0.0"}]""";

    // 待ち受けを開き直す上限 (StartListener)
    private const int ListenAttempts = 5;

    private readonly HttpListener _listener;
    private readonly Func<string, bool> _audioQueryFails;
    private readonly HttpStatusCode _audioQueryFailureStatus;
    private readonly TimeSpan _initializeSpeakerDelay;
    private readonly TimeSpan _audioQueryDelay;
    private readonly Task _serveTask;
    private int _audioQueryRequestCount;
    private readonly Func<bool> _versionAvailable;

    /// <param name="audioQueryFails">true を返したテキストだけ /audio_query を失敗させる</param>
    /// <param name="audioQueryFailureStatus">失敗させるときのステータス。既定の 400 はリトライ対象外</param>
    /// <param name="initializeSpeakerDelay">/initialize_speaker の応答を遅らせる時間 (モデルの読み込み)</param>
    /// <param name="audioQueryDelay">/audio_query の応答を遅らせる時間 (応答しないエンジン)</param>
    /// <param name="versionAvailable">false の間は /version を 503 にする (止まっているエンジンとして自動起動させる)。null なら常に成功</param>
    public FakeEngineServer(
        Func<string, bool>? audioQueryFails = null,
        HttpStatusCode audioQueryFailureStatus = HttpStatusCode.BadRequest,
        TimeSpan initializeSpeakerDelay = default,
        TimeSpan audioQueryDelay = default,
        Func<bool>? versionAvailable = null)
    {
        _versionAvailable = versionAvailable ?? (() => true);
        _audioQueryFails = audioQueryFails ?? (_ => false);
        _audioQueryFailureStatus = audioQueryFailureStatus;
        _initializeSpeakerDelay = initializeSpeakerDelay;
        _audioQueryDelay = audioQueryDelay;
        (_listener, BaseUrl) = StartListener();
        _serveTask = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>/audio_query を受けた回数 (リトライも 1 回ずつ数える)。</summary>
    public int AudioQueryRequestCount => Volatile.Read(ref _audioQueryRequestCount);

    /// <summary>最後に受けた /initialize_speaker のクエリ文字列 (<c>?</c> から)。</summary>
    public string? LastInitializeSpeakerQuery { get; private set; }

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
    /// <param name="connectionTimeoutSeconds">VoiceVox.ConnectionTimeout</param>
    public VoiceVoxApiClient CreateApiClient(int connectionTimeoutSeconds = 30)
    {
        var settings = CreateSettings();
        settings.ConnectionTimeout = connectionTimeoutSeconds;
        return new(settings, new RetryPolicyService(NullLogger.Instance, baseDelayMs: 1, maxDelayMs: 1), NullLogger.Instance);
    }

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
            var delay = context.Request.Url!.AbsolutePath switch
            {
                "/initialize_speaker" => _initializeSpeakerDelay,
                "/audio_query" => _audioQueryDelay,
                _ => TimeSpan.Zero,
            };
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }
            var (status, body) = Respond(context.Request);
            try
            {
                context.Response.StatusCode = (int)status;
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // 応答を遅らせている間にクライアントがタイムアウトで切断した (または Dispose された)
            }
        }
    }

    private (HttpStatusCode Status, byte[] Body) Respond(HttpListenerRequest request)
    {
        var path = request.Url!.AbsolutePath;
        if (path == "/audio_query")
        {
            Interlocked.Increment(ref _audioQueryRequestCount);
        }
        else if (path == "/initialize_speaker")
        {
            LastInitializeSpeakerQuery = request.Url.Query;
        }

        return path switch
        {
            "/version" when !_versionAvailable() => (HttpStatusCode.ServiceUnavailable, []),
            "/version" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes("\"0.0.0\"")),
            "/speakers" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(SpeakersJson)),
            "/initialize_speaker" => (HttpStatusCode.NoContent, []),
            "/audio_query" when _audioQueryFails(request.QueryString["text"] ?? "") => (_audioQueryFailureStatus, []),
            "/audio_query" => (HttpStatusCode.OK, Encoding.UTF8.GetBytes(AudioQueryJson)),
            "/synthesis" => (HttpStatusCode.OK, TestAudio.SineWav(24000, 200)),
            _ => (HttpStatusCode.NotFound, []),
        };
    }

    /// <summary>
    /// 空いているポートで待ち受けを始める。開けなければ (<see cref="ListenAttempts"/> 回まで) 別のポートでやり直す。
    /// </summary>
    /// <remarks>
    /// <see cref="GetFreePort"/> で調べたポートは閉じてから開き直すので、その間に並列に走る他のテスト (や他のプロセス) に取られうる。
    /// Start に失敗した <see cref="HttpListener"/> は使い回せないので、試すたびに作り直す。
    /// </remarks>
    private static (HttpListener Listener, string BaseUrl) StartListener()
    {
        for (var attempt = 1; ; attempt++)
        {
            var baseUrl = $"http://127.0.0.1:{GetFreePort()}";
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUrl + "/");
            try
            {
                listener.Start();
                return (listener, baseUrl);
            }
            catch (HttpListenerException) when (attempt < ListenAttempts)
            {
                listener.Close();
            }
        }
    }

    /// <summary>
    /// 空いているポート番号。調べた後は閉じるので、止まっているエンジンの URL には使わない (<see cref="StoppedEngine"/> を使う)。
    /// </summary>
    private static int GetFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
