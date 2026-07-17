namespace Ltb.App;

internal sealed record AlvrAvailabilitySnapshot(bool IsAvailable, string Diagnostic);

internal interface IAlvrAvailabilityProbe
{
    Task<AlvrAvailabilitySnapshot> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Proves that the local ALVR dashboard API is serving a version response. The
/// default URI uses ALVR's default web-server port and is loopback-only.
/// </summary>
internal sealed class AlvrLocalDashboardProbe : IAlvrAvailabilityProbe, IDisposable
{
    internal static readonly Uri DefaultBaseAddress = new("http://127.0.0.1:8082/");
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public AlvrLocalDashboardProbe(Uri? baseAddress = null, HttpClient? client = null)
    {
        var address = baseAddress ?? DefaultBaseAddress;
        if (!address.IsAbsoluteUri || !IPAddressIsLoopback(address.Host))
        {
            throw new ArgumentException(
                "The ALVR dashboard probe must use an absolute loopback URI.",
                nameof(baseAddress));
        }

        _client = client ?? new HttpClient();
        _ownsClient = client is null;
        _client.BaseAddress = address;
    }

    public async Task<AlvrAvailabilitySnapshot> ProbeAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            using var response = await _client.GetAsync("api/version", linked.Token)
                .ConfigureAwait(false);
            var version = await response.Content.ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(version))
            {
                return new AlvrAvailabilitySnapshot(
                    true,
                    $"ALVR local dashboard version endpoint responded on {_client.BaseAddress}");
            }

            return new AlvrAvailabilitySnapshot(
                false,
                $"ALVR local dashboard {_client.BaseAddress}api/version returned " +
                $"HTTP {(int)response.StatusCode} or an empty version response");
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new AlvrAvailabilitySnapshot(
                false,
                $"ALVR local dashboard {_client.BaseAddress}api/version did not respond " +
                $"within {ProbeTimeout.TotalMilliseconds:R} ms");
        }
        catch (HttpRequestException exception)
        {
            return new AlvrAvailabilitySnapshot(
                false,
                $"ALVR local dashboard {_client.BaseAddress}api/version is unavailable: " +
                exception.Message);
        }

    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static bool IPAddressIsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host, out var address) &&
         System.Net.IPAddress.IsLoopback(address));
}
