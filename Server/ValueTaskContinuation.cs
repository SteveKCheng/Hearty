using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace JobBank.Server
{
    /// <summary>
    /// Invokes continuations for an implementation of <see cref="IValueTaskSource"/>.
    /// </summary>
    /// <remarks>
    /// Normally Microsoft would have us use <see cref="ManualResetValueTaskSourceCore{TResult}"/>.
    /// However, that structure is clumsy in implementing shared promises, where the
    /// published result is accessed with an indirection, and locking (or another
    /// concurrency technique) is required to register the continuation in a shared
    /// location.  This structure provides a replacement that has all unnecessary code
    /// stripped out.
    /// </remarks>
    internal struct ValueTaskContinuation
    {
        /// <summary>
        /// The delegate for the continuation passed to <see cref="IValueTaskSource.OnCompleted" />.
        /// </summary>
        private Action<object?> _action;

        /// <summary>
        /// The arbitrary state object for the continuation passed to <see cref="IValueTaskSource.OnCompleted" />.
        /// </summary>
        private object? _argument;

        /// <summary>
        /// Execution context captured at construction, if it exists and is required to invoke
        /// the continuation.
        /// </summary>
        private ExecutionContext? _executionContext;

        /// <summary>
        /// May point to <see cref="SynchronizationContext"/> or <see cref="TaskScheduler"/>
        /// if the continuation is asked to run there; otherwise null.
        /// </summary>
        private object? _scheduler;

        /// <summary>
        /// Whether this instance has been properly initialized and can have its continuation invoked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// There can be at most one call to <see cref="Invoke" /> or <see cref="InvokeIgnoringExecutionContext" />
        /// unless this instance is initialized again.  Otherwise this structure will behave incorrectly.
        /// </para>
        /// <para>
        /// After one of the above two methods are called, this instance gets reset back to its
        /// default-initialized state which holds no continuation.  Note that the transition
        /// of state is not atomic, so this property is useful only if the caller takes locks
        /// to protect this structure.
        /// </para>
        /// </remarks>
        public bool IsValid => _action != null;

        /// <summary>
        /// Store the continuation passed from <see cref="IValueTaskSource.OnCompleted" />
        /// and capture any required contexts.
        /// </summary>
        public ValueTaskContinuation(Action<object?> action, object? argument, ValueTaskSourceOnCompletedFlags flags)
        {
            _action = action;
            _argument = argument;

            _executionContext = null;
            _scheduler = null;

            if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
                _executionContext = ExecutionContext.Capture();

            if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            {
                SynchronizationContext? syncContext = SynchronizationContext.Current;
                if (syncContext != null && syncContext.GetType() != typeof(SynchronizationContext))
                {
                    _scheduler = syncContext;
                }
                else
                {
                    TaskScheduler? taskScheduler = TaskScheduler.Current;
                    if (taskScheduler != TaskScheduler.Default)
                        _scheduler = taskScheduler;
                }
            }
        }

        /// <summary>
        /// Invoke the continuation.
        /// </summary>
        /// <remarks>
        /// <see cref="IsValid"/> must be true before calling this method.
        /// </remarks>
        /// <param name="forceAsync">
        /// This parameter only takes effect when no scheduler context has been captured.
        /// If this argument is true, then the continuation will be queued to run 
        /// in the thread pool; otherwise it will be run synchronously within this method.
        /// </param>
        public void Invoke(bool forceAsync)
        {
            var executionContext = _executionContext;

            if (executionContext == null)
            {
                InvokeIgnoringExecutionContext(forceAsync);
            }
            else
            {
                ExecutionContext.Run(executionContext,
                                     static s =>
                                     {
                                         ref var t = ref Unsafe.Unbox<(ValueTaskContinuation self, bool forceAsync)>(s!);
                                         t.self.InvokeIgnoringExecutionContext(t.forceAsync);
                                     },
                                     (this, forceAsync));
            }
        }

        /// <summary>
        /// Invoke the continuation without restoring .NET's execution context
        /// even if one has been captured.
        /// </summary>
        /// <remarks>
        /// <see cref="IsValid"/> must be true before calling this method.
        /// </remarks>
        /// <param name="forceAsync">
        /// This parameter only takes effect when no scheduler context has been captured.
        /// If this argument is true, then the continuation will be queued to run 
        /// in the thread pool; otherwise it will be run synchronously within this method.
        /// </param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD001:Avoid legacy thread switching APIs")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD110:Observe result of async calls")]
        public void InvokeIgnoringExecutionContext(bool forceAsync)
        {
            var action = _action;
            var argument = _argument;
            var scheduler = _scheduler;
            bool hasExecutionContext = (_executionContext != null);

            // Clear instance variables to allow garbage collection
            this = default;

            if (scheduler != null)
            {
                if (scheduler is SynchronizationContext syncContext)
                {
                    syncContext.Post(static s =>
                    {
                        var t = (Tuple<Action<object?>, object?>)s!;
                        t.Item1(t.Item2);
                    }, Tuple.Create(action, argument));
                }
                else
                {
                    var taskScheduler = Unsafe.As<TaskScheduler>(scheduler);
                    Task.Factory.StartNew(action, argument, 
                                          CancellationToken.None, 
                                          TaskCreationOptions.DenyChildAttach, 
                                          taskScheduler);
                }
            }
            else if (forceAsync)
            {
                if (!hasExecutionContext)
                    ThreadPool.UnsafeQueueUserWorkItem(action, argument, preferLocal: true);
                else
                    ThreadPool.QueueUserWorkItem(action, argument, preferLocal: true);
            }
            else
            {
                action(argument);
            }
        }
    }
}
