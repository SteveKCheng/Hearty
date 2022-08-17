using System;
using System.Net.Http;
using Polly;

namespace Hearty.PollyProxies;

/// <summary>
/// Extends certain .NET types with Polly-specific information.
/// </summary>
public static class PollyProxiesExtensions
{
    /// <summary>
    /// Get the Polly execution context attached to an HTTP request message, if any.
    /// </summary>
    /// <param name="request">The HTTP request message. </param>
    /// <returns>
    /// The execution context if it has been set into <paramref name="request" />
    /// by <see cref="SetPolicyExecutionContext" />, otherwise null.
    /// </returns>
    public static Context? GetPolicyExecutionContext(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.TryGetValue<Context>(new(PolicyExecutionContextKey), out var context);
        return context;
    }

    /// <summary>
    /// Attach a Polly execution context into an HTTP request message.
    /// </summary>
    /// <param name="request">The HTTP request message. </param>
    /// <param name="context">The execution context to attach. </param>
    public static void SetPolicyExecutionContext(this HttpRequestMessage request, Context? context)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(new(PolicyExecutionContextKey), context);
    }

    private static readonly string PolicyExecutionContextKey = "PolicyExecutionContext";
}
