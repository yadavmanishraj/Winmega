namespace Omega.Core.Upstream;

/// <summary>
/// Raised when the upstream endpoint cannot be reached, returns a
/// non-success HTTP status, or answers with a body that lacks the keys
/// the call requires. (Upstream reports many errors as HTTP 200 with an
/// error/empty body — see UPSTREAM_SPEC §7 — so "not found" conditions
/// surface here too, with <see cref="Call"/> set.)
/// </summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(string message, string? call = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Call = call;
    }

    /// <summary>The <c>__call</c> endpoint name that failed, when known.</summary>
    public string? Call { get; }
}
