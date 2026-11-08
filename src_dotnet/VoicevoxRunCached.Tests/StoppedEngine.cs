using System.Net;
using System.Net.Sockets;

namespace VoicevoxRunCached.Tests;

/// <summary>
/// 止まっているエンジンの URL。Dispose するまで、その URL への接続は必ず拒否される。
/// </summary>
/// <remarks>
/// 空きポートを調べて閉じただけの URL だと、並列に走る <see cref="FakeEngineServer"/> がそのポートで待ち受けを始め、
/// 止まっているはずのエンジンが応答することがある。ここでは bind したまま listen しないソケットでポートを持ち続ける
/// (他からは使えず、接続は拒否される)。
/// </remarks>
internal sealed class StoppedEngine : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    {
        ExclusiveAddressUse = true,
    };

    public StoppedEngine()
    {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_socket.LocalEndPoint!).Port}";
    }

    public string BaseUrl { get; }

    public void Dispose() => _socket.Dispose();
}
