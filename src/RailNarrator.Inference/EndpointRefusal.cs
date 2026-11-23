using System.Net;

namespace RailNarrator.Inference;

/// <summary>
/// An endpoint's refusal in words for the reader: what its status means, then the host (the cause
/// first, because a status bar cuts a long line short). Never the
/// response body, which some servers fill with part of the key they were sent.
/// </summary>
public static class EndpointRefusal
{
    /// <summary>True for a status that is about the account or the server, not about one request.</summary>
    public static bool IsEndpoints(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <param name="status">Null when there was no answer at all.</param>
    public static string Describe(string host, HttpStatusCode? status) => status switch
    {
        null => $"No answer from {host}.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"API key refused ({(int)status}) by {host}.",
        HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests
            => $"Out of quota or rate-limited ({(int)status}) at {host}.",
        HttpStatusCode.NotFound => $"Address or model not known (404) at {host}.",
        _ => $"Server error ({(int)status}) from {host}.",
    };
}
