using System.Net;
using Ltb.Alvr;

namespace Ltb.Integration.Tests;

public sealed class AlvrAvailabilityProbeTests
{
    [Fact]
    public async Task AcceptsNonEmptyVersionFromLoopbackEndpoint()
    {
        using var client = Client(HttpStatusCode.OK, "{\"version\":\"20.12.1\"}");
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsAvailable, result.Diagnostic);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "missing")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task RejectsMissingOrEmptyVersion(HttpStatusCode status, string body)
    {
        using var client = Client(status, body);
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://localhost:8082/"),
            client);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public async Task SendsAlvrHeaderRequiredByModernAlvr()
    {
        using var handler = new HeaderCapturingHandler();
        using var client = new HttpClient(handler);
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsAvailable, result.Diagnostic);
        Assert.Equal("true", handler.AlvrHeaderValue);
    }

    [Fact]
    public async Task HeaderlessStyleBadRequestRejectionMapsToUnavailable()
    {
        using var client = Client(HttpStatusCode.BadRequest, "missing X-ALVR header");
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("HTTP 400", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            "api/version",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonLoopbackDashboardUri()
    {
        Assert.Throws<ArgumentException>(() =>
            new AlvrLocalDashboardProbe(new Uri("http://192.0.2.10:8082/")));
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBecomingUnavailable()
    {
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);
        using var cancellation = new CancellationTokenSource();

        var pendingProbe = probe.ProbeAsync(cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingProbe);
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task InternalFiveHundredMillisecondTimeoutMapsToUnavailable()
    {
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await probe.ProbeAsync(CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.IsAvailable);
        Assert.Contains("within 500 ms", result.Diagnostic, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"The bounded ALVR probe took {stopwatch.Elapsed}.");
        await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HttpRequestFailureMapsToUnavailable()
    {
        using var client = new HttpClient(new ThrowingHandler(
            new HttpRequestException("synthetic loopback refusal")));
        using var probe = new AlvrLocalDashboardProbe(
            new Uri("http://127.0.0.1:8082/"),
            client);

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("is unavailable", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("synthetic loopback refusal", result.Diagnostic, StringComparison.Ordinal);
    }

    private static HttpClient Client(HttpStatusCode status, string body) =>
        new(new StubHandler(status, body));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("http://127.0.0.1:8082/api/version", request.RequestUri?.ToString()
                .Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("true", Assert.Single(request.Headers.GetValues("X-ALVR")));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public string? AlvrHeaderValue { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AlvrHeaderValue = request.Headers.TryGetValues("X-ALVR", out var values)
                ? string.Join(",", values)
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("20.14.1"),
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking handler completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
