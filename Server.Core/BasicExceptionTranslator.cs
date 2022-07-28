using System;
using System.Threading.Tasks;
using Hearty.Common;

namespace Hearty.Server;

/// <summary>
/// Translates a .NET exception into <see cref="PromiseExceptionalData" />
/// so that it can be exposed remotely as promise output.
/// </summary>
public static class BasicExceptionTranslator
{
    private static ValueTask<PromiseData> Translate(object state, PromiseId? promiseId, Exception exception)
    {
        PromiseData promiseData = (exception is RemoteWorkException remoteWorkException)
                                    ? new PromiseExceptionalData(remoteWorkException.Payload)
                                    : new PromiseExceptionalData(exception);

        return ValueTask.FromResult(promiseData);
    }

    /// <summary>
    /// Singleton instance of <see cref="PromiseExceptionTranslator" />
    /// implemented by this class.
    /// </summary>
    public static PromiseExceptionTranslator Instance { get; } = Translate;
}
