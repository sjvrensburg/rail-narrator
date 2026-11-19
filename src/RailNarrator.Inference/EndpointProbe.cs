using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RailNarrator.Inference;

/// <summary>What a probe of an OpenAI-compatible endpoint found.</summary>
public enum EndpointProbeStatus
{
    /// <summary>The endpoint answered and lists the model.</summary>
    Ready,

    /// <summary>The endpoint answered, but its model list does not include the model named.</summary>
    ModelNotListed,

    /// <summary>The endpoint refused the key (401 or 403), or wants one and none was given.</summary>
    KeyRefused,

    /// <summary>Nothing answered at the address, or it timed out.</summary>
    Unreachable,

    /// <summary>Something answered, but not as an OpenAI-compatible endpoint does.</summary>
    UnexpectedAnswer,

    /// <summary>No address or no model was given.</summary>
    NotConfigured,
}

/// <param name="Status">What was found.</param>
/// <param name="Message">One sentence for the reader. Never contains the key.</param>
/// <param name="Models">The model ids the endpoint offers, when it listed them.</param>
public sealed record EndpointProbeResult(EndpointProbeStatus Status, string Message, IReadOnlyList<string> Models)
{
    public bool IsReady => Status == EndpointProbeStatus.Ready;
}

/// <summary>
/// Checks an OpenAI-compatible endpoint for the Settings dialogue, with values that may not be saved
/// yet, and says what is wrong when something is: the address, the key or the model name.
/// </summary>
/// <remarks>
/// The probe is <c>GET models</c>: part of the OpenAI-compatible contract, it needs no tokens and
/// costs nothing on a metered endpoint. It cannot show that the model accepts images; the vision
/// path still finds that out on its first request.
/// </remarks>
public static class EndpointProbe
{
    public static async Task<EndpointProbeResult> ProbeAsync(
        string? baseUrl, string? model, string? apiKey, HttpMessageHandler? handler = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return new(EndpointProbeStatus.NotConfigured, "Give both the endpoint address and the model name.", []);
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https"))
            return new(EndpointProbeStatus.NotConfigured, $"'{baseUrl.Trim()}' is not an http or https address.", []);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = address;
        http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return new(EndpointProbeStatus.Unreachable, $"No answer from {address.Host} within {http.Timeout.TotalSeconds:F0} s.", []);
        }
        catch (HttpRequestException ex)
        {
            return new(EndpointProbeStatus.Unreachable, $"Could not reach {address.Host}: {ex.Message}", []);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(EndpointProbeStatus.KeyRefused, string.IsNullOrWhiteSpace(apiKey)
                    ? $"{address.Host} wants an API key, and none is set."
                    : $"{address.Host} refused the API key ({(int)response.StatusCode}).", []);
            if (!response.IsSuccessStatusCode)
                return new(EndpointProbeStatus.UnexpectedAnswer,
                    $"{address.Host} answered {(int)response.StatusCode} {response.ReasonPhrase} to a request for its models. Is the address the API's base (often ending in /v1)?", []);

            List<string> models;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                models = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    ? data.EnumerateArray()
                        .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                        .OfType<string>().Order(StringComparer.Ordinal).ToList()
                    : throw new JsonException("no 'data' list");
            }
            catch (JsonException)
            {
                return new(EndpointProbeStatus.UnexpectedAnswer,
                    $"{address.Host} answered, but not with an OpenAI-compatible model list. Is the address the API's base (often ending in /v1)?", []);
            }

            string wanted = model.Trim();
            if (models.Contains(wanted, StringComparer.Ordinal))
                return new(EndpointProbeStatus.Ready, $"{address.Host} answered and offers {wanted}.", models);
            // Some servers (a single-model llama.cpp, some gateways) list one name and serve any.
            return new(EndpointProbeStatus.ModelNotListed, models.Count == 0
                ? $"{address.Host} answered but lists no models; it may still serve {wanted}."
                : $"{address.Host} answered but does not list {wanted}. It offers: {string.Join(", ", models.Take(8))}{(models.Count > 8 ? ", …" : "")}.",
                models);
        }
    }
}
