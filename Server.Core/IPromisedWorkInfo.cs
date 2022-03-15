namespace Hearty.Server;

/// <summary>
/// Provides a subset of the information from 
/// <see cref="PromisedWork" /> for monitoring purposes only.
/// </summary>
public interface IPromisedWorkInfo
{
    /// <summary>
    /// A string that may be used to select a sub-function 
    /// to apply to the data.
    /// </summary>
    /// <remarks>
    /// See <see cref="PromisedWork.Route" />.
    /// </remarks>
    string? Route { get; }

    /// <summary>
    /// A condensed string representation, expected to be
    /// unique within a context, of the promise or the work.
    /// </summary>
    /// <remarks>
    /// See <see cref="PromisedWork.Path" />.
    /// </remarks>
    string? Path { get; }

    /// <summary>
    /// The ID of the promise object that originated this work.
    /// </summary>
    /// <remarks>
    /// See <see cref="PromisedWork.Promise" />.
    /// </remarks>
    PromiseId? PromiseId { get; }

    /// <summary>
    /// The initial estimate of the amount of time the job
    /// would take, in milliseconds.
    /// </summary>
    /// <remarks>
    /// See <see cref="PromisedWork.InitialWait" />.
    /// </remarks>
    int InitialWait { get; }

    /// <summary>
    /// Get a property keyed by a string 
    /// that a monitoring tool that display.
    /// </summary>
    /// <remarks>
    /// The monitoring tool can select a handful of
    /// such properties to display in a table,
    /// one column for each selected property,
    /// with one row for each active work item.
    /// </remarks>
    /// <param name="key">Name of the property. </param>
    /// <returns>
    /// Value of the property.  Commonly values will
    /// be strings or scalars; a monitoring tool
    /// is not expected to be able to display complex values.
    /// </returns>
    object? GetDisplayProperty(string key);
}
