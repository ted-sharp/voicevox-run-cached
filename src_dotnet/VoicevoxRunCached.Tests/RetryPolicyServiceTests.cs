using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using VoicevoxRunCached.Services.Infrastructure;

namespace VoicevoxRunCached.Tests;

public class RetryPolicyServiceTests
{
    /// <summary>テスト時の遅延を最小化するためベース 1ms / 最大試行 3 回の RetryPolicy を作成。</summary>
    private static RetryPolicyService Create(int maxAttempts = 3)
        => new(NullLogger.Instance, baseDelayMs: 1, maxDelayMs: 5, maxRetryAttempts: maxAttempts);

    [Test]
    public async Task Success_OnFirstTry_DoesNotRetry()
    {
        var policy = Create();
        var calls = 0;

        var result = await policy.ExecuteWithRetryAsync(() =>
        {
            calls++;
            return Task.FromResult(42);
        }, "op");

        await Assert.That(result).IsEqualTo(42);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task NonRetryableException_PropagatesImmediately()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new ArgumentException("bad");
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task HttpRequestException_4xx_DoesNotRetry()
    {
        // 401 はリトライ対象外
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized);
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task HttpRequestException_5xx_RetriesUpToMaxAttempts()
    {
        var policy = Create(maxAttempts: 3);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task HttpRequestException_5xx_ThenSuccess_ReturnsResult()
    {
        var policy = Create();
        var calls = 0;

        var result = await policy.ExecuteWithRetryAsync(() =>
        {
            calls++;
            if (calls < 2)
            {
                throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            }
            return Task.FromResult("ok");
        }, "op");

        await Assert.That(result).IsEqualTo("ok");
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task TooManyRequests_IsRetried()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new HttpRequestException("rate limit", null, HttpStatusCode.TooManyRequests);
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task IOException_IsRetried()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new IOException("io");
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task SocketException_IsRetried()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new SocketException();
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task ExceptionMessageMatchingConnection_IsRetried()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new InvalidOperationException("Connection refused");
            }, "op");
        });

        // "connection" を含むので ShouldRetryException で true
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task TaskCanceledException_AfterUserCancel_RethrowsAsOperationCancelled()
    {
        var policy = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
                throw new TaskCanceledException("cancelled"),
                "op",
                cts.Token);
        });
    }

    [Test]
    public async Task TaskCanceledException_WithoutUserCancel_IsRetried()
    {
        var policy = Create();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
            {
                calls++;
                throw new TaskCanceledException("timeout");
            }, "op");
        });

        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task FinalFailure_WrapsLastExceptionAsInner()
    {
        var policy = Create();
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await policy.ExecuteWithRetryAsync<int>(() =>
                throw new HttpRequestException("server error", null, HttpStatusCode.BadGateway),
                "op");
        });

        await Assert.That(caught!.InnerException).IsTypeOf<HttpRequestException>();
    }
}
