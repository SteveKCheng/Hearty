using Hearty.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Hearty.Scheduling;

/// <summary>
/// Implementation of <see cref="IValueTaskSource{T}" /> that can be reset,
/// and allows concurrently running producers.
/// </summary>
/// <remarks>
/// <para>
/// This class provides the parts of the implementation 
/// of <see cref="ResettableConcurrentTaskSource{T}"/> that are common to
/// all types, and itself can be used for a void-returning task source.
/// </para>
/// <para>
/// The implementation avoids locks so that continuations can be safely
/// invoked synchronously.  Also, had locks been used, they would also
/// have to be recursive to work properly when registering callbacks
/// with <see cref="CancellationToken" />.
/// Recursive locks are, of course, widely agreed to be problematic.
/// </para>
/// </remarks>
internal class ResettableConcurrentTaskSource : IValueTaskSource
{
    [Flags]
    private enum Stage : ushort
    {
        /// <summary>
        /// The task has been set to signal success.
        /// </summary>
        Success = 0x0,

        /// <summary>
        /// The task has been cancelled from a CancellationToken triggering.
        /// </summary>
        Cancelled = 0x1,

        /// <summary>
        /// The task has been set to signal a fault.
        /// </summary>
        Faulted = 0x2,

        /// <summary>
        /// The task source is being prepared: there may be registration of the
        /// CancellationToken.
        /// </summary>
        Preparing = 0x4,

        /// <summary>
        /// The task source is ready to accept asynchronous activation,
        /// but has no continuation attached.
        /// </summary>
        Pending = 0x8,

        /// <summary>
        /// The task source is ready to accept asynchronous activation,
        /// and it should call the continuation when triggered.
        /// </summary>
        HasContinuation = 0x10,

        /// <summary>
        /// The task source has just first activated since the last transition
        /// to pending status.
        /// </summary>
        Activating = 0x20,
    }

    private CancellationTokenRegistration _cancellationRegistration;
    private ValueTaskContinuation _continuation;
    private uint _state;
    protected Exception? _exception;

    /// <summary>
    /// Prepare to reset this task source, incrementing the version.
    /// </summary>
    /// <returns>The new token (version) for this task source object
    /// to store in <see cref="ValueTask"/>. </returns>
    private short Prepare()
    {
        uint state = _state;
        uint oldState;
        ushort token;

        do
        {
            token = (ushort)(state & 0xFFFF);
            var stage = (Stage)(ushort)(state >> 16);

            if (stage > Stage.Faulted)
                throw new InvalidOperationException();

            unchecked { ++token; }
            oldState = state;
            
            uint newState = (uint)token | (((uint)Stage.Preparing) << 16);

            state = Interlocked.CompareExchange(ref _state, newState, oldState);
        } while (state != oldState);

        return (short)token;
    }

    private void TransitionInfallibly(Stage newStage, short token)
    {
        uint state = (ushort)token | ((uint)newStage << 16);
        Volatile.Write(ref _state, state);
    }

    private bool TryTransition(Stage oldStage, Stage newStage)
        => TryTransition(ref oldStage, newStage, out _);

    private bool TryTransition(ref Stage oldStage, Stage newStage, out short token)
    {
        uint state = _state;
        uint oldState;
        Stage stage;
        do
        {
            uint utoken = (ushort)(state & 0xFFFF);
            stage = (Stage)(ushort)(state >> 16);
            uint newState = utoken | (((uint)newStage) << 16);
            token = (short)utoken;
            if ((stage & oldStage) == 0)
                return false;

            oldState = state;
            state = Interlocked.CompareExchange(ref _state, newState, state);
        } while (oldState != state);

        oldStage = stage;
        return true;
    }

    private ValueTaskContinuation TakeContinuation()
    {
        var c = _continuation;
        _continuation = default;
        return c;
    }

    private Stage GetStage(out short token)
    {
        uint state = _state;
        token = (short)(state & 0xFFFF);
        return (Stage)(ushort)(state >> 16);
    }

    /// <summary>
    /// Implements <see cref="IValueTaskSource.GetStatus" />.
    /// </summary>
    protected ValueTaskSourceStatus GetStatus(short token)
    {
        var stage = GetStage(out var currentToken);
        if (currentToken != token)
            throw new InvalidOperationException();

        return stage switch
        {
            Stage.Success => ValueTaskSourceStatus.Succeeded,
            Stage.Faulted => ValueTaskSourceStatus.Faulted,
            Stage.Cancelled => ValueTaskSourceStatus.Canceled,
            _ => ValueTaskSourceStatus.Pending
        };
    }

    /// <summary>
    /// Implements <see cref="IValueTaskSource.OnCompleted" />.
    /// </summary>
    protected void OnCompleted(Action<object?> action,
                               object? state,
                               short token,
                               ValueTaskSourceOnCompletedFlags flags)
    {
        var stage = GetStage(out var currentToken);
        if (currentToken != token || stage == Stage.HasContinuation)
            throw new InvalidOperationException();

        var continuation = new ValueTaskContinuation(action, state, flags);
        _continuation = continuation;

        if (!TryTransition(Stage.Pending, Stage.HasContinuation))
        {
            _continuation = default;
            continuation.InvokeIgnoringExecutionContext(forceAsync: false);
        }
    }

    /// <summary>
    /// Retrieve the result for a successful task or throw an exception
    /// if it failed.
    /// </summary>
    /// <typeparam name="T">The type of the result. </typeparam>
    /// <param name="token">The version of this instance that was 
    /// set for the asynchronous operation,
    /// which is verified against the current version. </param>
    /// <param name="storage">Where the result is stored if
    /// the task is successful. </param>
    /// <returns>
    /// A copy of the value from <paramref name="storage"/> if
    /// the task is successful.
    /// </returns>
    protected T GetResultCore<T>(short token, ref T storage)
    {
        var stage = GetStage(out var currentToken);
        if (currentToken != token)
            throw new InvalidOperationException();

        return stage switch
        {
            Stage.Success => storage,
            Stage.Faulted => throw _exception!,
            Stage.Cancelled => throw new OperationCanceledException(),
            _ => throw new InvalidOperationException()
        };
    }

    /// <summary>
    /// Reset this instance to represent another asynchronous operation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to register
    /// a callback to cancel the task early.
    /// </param>
    /// <returns>The new token (version) for this task source object
    /// to store in <see cref="ValueTask"/>. </returns>
    protected short ResetCore(CancellationToken cancellationToken)
    {
        var token = Prepare();

        _cancellationRegistration = cancellationToken.Register(s => {
            var self = Unsafe.As<ResettableConcurrentTaskSource>(s!);
            var oldStage = Stage.Preparing | Stage.Pending | Stage.HasContinuation;
            if (self.TryTransition(ref oldStage, Stage.Activating, out var token))
            {
                var c = self.TakeContinuation();
                self.TransitionInfallibly(Stage.Cancelled, token);

                if (oldStage == Stage.HasContinuation)
                    c.InvokeIgnoringExecutionContext(forceAsync: true);
            }
        }, this);

        // Transition fails if cancellationToken synchronously runs
        // the callback above.
        if (!TryTransition(Stage.Preparing, Stage.Pending))
            cancellationToken.ThrowIfCancellationRequested();

        return token;
    }

    /// <summary>
    /// Set the task to completed and invoke any attached continuations.
    /// </summary>
    protected bool TrySetResultCore<T>(T result, ref T storage)
    {
        var oldStage = Stage.Pending | Stage.HasContinuation;
        if (TryTransition(ref oldStage, Stage.Activating, out var token))
        {
            _cancellationRegistration.Dispose();
            storage = result;

            var c = TakeContinuation();
            TransitionInfallibly(Stage.Success, token);

            if (oldStage == Stage.HasContinuation)
                c.InvokeIgnoringExecutionContext(forceAsync: false);

            return true;
        }

        return false;
    }

    void IValueTaskSource.GetResult(short token)
    {
        bool dummy = false;
        GetResultCore(token, ref dummy);
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        => GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, 
                                      object? state, 
                                      short token, 
                                      ValueTaskSourceOnCompletedFlags flags)
        => OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// Implementation of <see cref="IValueTaskSource{T}" /> that can be reset,
/// and allows concurrently running producers.
/// </summary>
/// <remarks>
/// The first producer that posts completion to this task source "wins".
/// This feature allows handling asynchronous cancellation.
/// </remarks>
/// <typeparam name="T">The type of the non-exceptional result to be
/// provided by the task source. </typeparam>
internal class ResettableConcurrentTaskSource<T> : ResettableConcurrentTaskSource, IValueTaskSource<T>
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private T _result;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    T IValueTaskSource<T>.GetResult(short token)
    {
        return GetResultCore(token, ref _result);
    }

    /// <summary>
    /// Set the task to completed and invoke any registered continuations.
    /// </summary>
    public bool TrySetResult(T result)
    {
        return TrySetResultCore(result, ref _result);
    }

    public ValueTask<T> Reset(CancellationToken cancellationToken)
    {
        var token = ResetCore(cancellationToken);
        return new ValueTask<T>(this, token);
    }

    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token)
        => base.GetStatus(token);

    void IValueTaskSource<T>.OnCompleted(Action<object?> continuation,
                                         object? state,
                                         short token,
                                         ValueTaskSourceOnCompletedFlags flags)
        => base.OnCompleted(continuation, state, token, flags);
}
