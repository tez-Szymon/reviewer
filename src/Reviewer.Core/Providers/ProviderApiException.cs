using System.Net;

namespace Reviewer.Core.Providers;

/// <summary>A hosting platform's API refused a request; each adapter throws its own subclass.</summary>
public abstract class ProviderApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
