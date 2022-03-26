using System;
using Hearty.Common;

namespace Hearty.Server;

/// <summary>
/// Translates a .NET exception into <see cref="PromiseExceptionalData" />
/// so that it can be exposed remotely as promise output.
/// </summary>
public static class BasicExceptionTranslator
{
    private static PromiseData Translate(PromiseId? promiseId, Exception exception)
    {
        // Unwrap exceptions from worker hosts executing the job
        if (exception is RemoteWorkException remoteWorkException)
            return new PromiseExceptionalData(remoteWorkException.Payload);
        else
            return new PromiseExceptionalData(exception);
    }

    /// <summary>
    /// Singleton instance of <see cref="PromiseExceptionTranslator" />
    /// implemented by this class.
    /// </summary>
    public static PromiseExceptionTranslator Instance { get; } = Translate;
}
