using System;
using System.Collections.Generic;
using Hearty.BTree;
using System.Threading;
using System.Numerics;
using Hearty.Utilities;

namespace Hearty.Server
{
    /// <summary>
    /// Allows queuing up objects to be processed at a later time,
    /// typically for expiring them from cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation uses the proper algorithms that can 
    /// scale to millions of items.  
    /// </para>
    /// <para>
    /// The implementation and interface of this class are, unfortunately,
    /// complex, so that this class can safely and correctly process 
    /// possibly conflicting changes to expiration times that may be 
    /// requested concurrently.  
    /// B+Trees are used instead of much simpler priority 
    /// heaps to allow accessing arbitrary entries to cancel them.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">
    /// The type of object subject to expirations.  This type must have some
    /// total ordering, and must be able to remember any registered
    /// expiration time.
    /// </typeparam>
    public class ExpiryQueue<T> where T: class
    {
        /// <summary>
        /// An element in the sorted sequence of targets to expire.
        /// </summary>
        private readonly struct Entry
        {
            /// <summary>
            /// The target object to expire; passed to the user-specified expiry functor.
            /// </summary>
            public readonly T Target;

            /// <summary>
            /// The recorded expiry time.
            /// </summary>
            public readonly DateTime Expiry;

            public Entry(T target, DateTime expiry)
            {
                Target = target;
                Expiry = expiry;
            }
        }

        /// <summary>
        /// Stores the targets to process sorted by expiry time, then by the target
        /// itself.
        /// </summary>
        private readonly BTreeMap<Entry, bool> _sortedEntries;

        /// <summary>
        /// The timer that fires when at the earliest time there is a target to expire.
        /// </summary>
        private readonly Timer _timer;

        /// <summary>
        /// The earliest expiry time seen in the requests processed so far.
        /// </summary>
        private DateTime _earliestExpiry;

        /// <summary>
        /// The user's function to invoke when a target item expires.
        /// </summary>
        private readonly Action<T> _expiryAction;

        /// <summary>
        /// Striped locks for serializing requests to change expiries
        /// for a given target object.
        /// </summary>
        /// <remarks>
        /// The number of locks is the degree of hardware concurrency
        /// possible, rounded up to the next power of 2.
        /// </remarks>
        private readonly StripedLocks _targetStripedLocks
            = StripedLocks<ExpiryQueue<T>>.Shared;

        /// <summary>
        /// Buffer to expire items in a batch.
        /// </summary>
        private readonly T[] _currentExpiredTargets;

        /// <summary>
        /// Orders the items by reverse chronological order of their expiry time.
        /// </summary>
        /// <remarks>
        /// When expiry times tie in the ordering, the items themselves
        /// are compared according to a user-specified comparer.
        /// </remarks>
        private class EntryComparer : IComparer<Entry>
        {
            private readonly IComparer<T> _targetComparer;

            public int Compare(ExpiryQueue<T>.Entry x, ExpiryQueue<T>.Entry y)
            {
                int c = x.Expiry.CompareTo(y.Expiry);
                if (c != 0)
                    return -c;

                return _targetComparer.Compare(x.Target, y.Target);
            }

            public EntryComparer(IComparer<T> targetComparer)
                => _targetComparer = targetComparer;
        }

        /// <summary>
        /// Remove up to a certain number of items from
        /// <see cref="_sortedEntries" /> and run 
        /// <see cref="_expiryAction" /> if the current time
        /// is after their expiry time.
        /// </summary>
        /// <returns>The expiry time for the next unprocessed item
        /// in the queue, or null if there are no more items.
        /// </returns>
        private DateTime? ExpireOneBatch(DateTime now)
        {
            DateTime? nextExpiry = null;
            int count = 0;
            var currentExpiredTargets = _currentExpiredTargets;

            lock (_sortedEntries)
            {
                var enumerator = _sortedEntries.GetEnumerator(toBeginning: false);

                try
                {
                    while (enumerator.MovePrevious())
                    {
                        var entry = enumerator.Current.Key;

                        // Quit loop if the next item, and therefore all items afterward,
                        // have not yet expired, or if enough items have been processed
                        // in this run.
                        if (entry.Expiry > now || count == currentExpiredTargets.Length)
                        {
                            nextExpiry = entry.Expiry;
                            break;
                        }
                            
                        enumerator.RemoveCurrent();

                        // Save in an array so we can invoke the expiry action
                        // outside the B+Tree lock.
                        currentExpiredTargets[count++] = entry.Target;
                    }
                }
                finally
                {
                    enumerator.Dispose();
                }
            }

            for (int i = 0; i < count; ++i)
            {
                ref var target = ref currentExpiredTargets[i];
                try
                {
                    _expiryAction.Invoke(target);
                }
                catch
                {
                    // FIXME log the error
                }

                target = null;
            }

            return nextExpiry;
        }

        /// <summary>
        /// Prepare to accept items to expire.
        /// </summary>
        /// <param name="targetComparer">
        /// Total ordering on the possible items, required for this implementation
        /// to maintain its sorted sequence of active items. 
        /// </param>
        /// <param name="expiryAction">
        /// A function called on an item when it expires.
        /// </param>
        public ExpiryQueue(IComparer<T> targetComparer, 
                           Action<T> expiryAction)
        {
            var entryComparer = new EntryComparer(targetComparer);
            _sortedEntries = new BTreeMap<Entry, bool>(32, entryComparer);
            _expiryAction = expiryAction;
            _timer = new Timer(s => OnTimerFiring(s), this, 
                               dueTime: Timeout.Infinite, 
                               period: Timeout.Infinite);

            _currentExpiredTargets = new T[10];
        }

        /// <summary>
        /// Callback function for <see cref="_timer"/> to expire
        /// items from <see cref="_sortedEntries" />.
        /// </summary>
        private static void OnTimerFiring(object? state)
        {
            var self = (ExpiryQueue<T>)state!;
            DateTime now;
            DateTime? nextExpiry;
            do
            {
                now = DateTime.UtcNow;
                nextExpiry = self.ExpireOneBatch(now);
            } while (nextExpiry < now);

            self.AdjustTimer(nextExpiry, now);
        }

        private void AdjustTimer(DateTime? expiry, DateTime now)
        {
            if (expiry == null)
                return;

            var bucketedExpiry = BucketExpiryTime(expiry.GetValueOrDefault());

            lock (_timer)
            {
                if (bucketedExpiry < _earliestExpiry)
                {
                    _earliestExpiry = bucketedExpiry;
                    int dueTime = GetDueTime(bucketedExpiry, now);
                    _timer.Change(dueTime, period: Timeout.Infinite);
                }
            }
        }

        private static DateTime BucketExpiryTime(DateTime expiry)
        {
            // FIXME avoid overflow
            var divisor = 10 * TimeSpan.TicksPerSecond;
            var ticks = (expiry.Ticks + (divisor - 1)) / divisor * divisor;
            return new DateTime(ticks);
        }

        private static int GetDueTime(DateTime expiry, DateTime now)
        {
            var ticks = (expiry - now).Ticks;
            if (ticks <= 0)
                return 0;

            ulong milliseconds = (ulong)ticks / (ulong)TimeSpan.TicksPerMillisecond;
            if (milliseconds <= int.MaxValue)
                return (int)milliseconds;

            return int.MaxValue;
        }

        /// <summary>
        /// Add, change or remove the expiration for a target item.
        /// </summary>
        /// <param name="target">The item that may be expired. </param>
        /// <param name="arg">Any argument desired to be passed into <paramref name="exchangeFunc" />.
        /// It probably should represent the new expiry time that is suggested or
        /// desired to be set. 
        /// </param>
        /// <param name="exchangeFunc">
        /// Function called to exchange the old and new record of the expiry time 
        /// within <paramref name="target" />.  For the same target, calls to this 
        /// "exchange function" will be serialized in the face of concurrent requests
        /// to change the expiry time.  It can resolve conflicting requests, e.g.
        /// by taking the maximum or minimum.  This function needs to record its
        /// decision inside <paramref name="target" /> so that it can report any
        /// old expiry setting, which is needed to cancel it.
        /// </param>
        /// <returns>
        /// The new expiry time as decided by <paramref name="exchangeFunc" />
        /// (based on <paramref name="arg" />.
        /// </returns>
        public DateTime? ChangeExpiry<TArg>(T target, in TArg arg, ExpiryExchangeFunc<T, TArg> exchangeFunc)
        {
            DateTime? newExpiry;

            // Serialize execution for any given target object
            lock (_targetStripedLocks[target.GetHashCode()])
            {
                newExpiry = exchangeFunc(target, arg, out var oldExpiry);

                newExpiry = EnsureInUtc(newExpiry);
                oldExpiry = EnsureInUtc(oldExpiry);

                if (newExpiry != oldExpiry)
                    ModifyEntry(target, oldExpiry, newExpiry);
            }

            // Schedule the expiry timer to fire when needed
            AdjustTimer(newExpiry, DateTime.UtcNow);

            return newExpiry;
        }

        private static DateTime? EnsureInUtc(DateTime? expiry)
        {
            if (expiry == null || expiry.GetValueOrDefault().Kind == DateTimeKind.Utc)
                return expiry;

            return expiry.GetValueOrDefault().ToUniversalTime();
        }

        /// <summary>
        /// Add, modify or remove an expiration entry in <see cref="_sortedEntries" />. 
        /// </summary>
        private void ModifyEntry(T target, DateTime? oldExpiry, DateTime? newExpiry)
        {
            lock (_sortedEntries)
            {
                if (oldExpiry != null)
                    _sortedEntries.Remove(new Entry(target, oldExpiry.GetValueOrDefault()));

                if (newExpiry != null)
                    _sortedEntries.Add(new Entry(target, newExpiry.GetValueOrDefault()), true);
            }
        }
    }

    /// <summary>
    /// A function called by <see cref="ExpiryQueue{T}.ChangeExpiry" /> to safely 
    /// effect a change of the expiry when there are concurrent requests on
    /// the same object.
    /// </summary>
    /// <typeparam name="T">The type of object to be expired. </typeparam>
    /// <typeparam name="TArg">The type of the arbitrary argument</typeparam>
    /// <param name="target">The object whose expiry is to be changed. </param>
    /// <param name="arg">An arbitrary argument passed through from 
    /// <see cref="ExpiryQueue{T}.ChangeExpiry" />, which can be taken
    /// as an input or suggestion as to how to change the expiry.
    /// </param>
    /// <param name="oldExpiry">
    /// The expiry time that had been registered in <see cref="ExpiryQueue{T}" />
    /// previously for <paramref name="target" />.  It is needed to look
    /// up its existing entry in the expiry queue to delete it, so that
    /// it can be re-added to the queue with the new expiry time.  Set to
    /// null if the object has not been registered before.
    /// </param>
    /// <returns>
    /// The desired new expiry time, or null if <paramref name="target" />
    /// should no longer be scheduled to be expired. 
    /// </returns>
    public delegate DateTime? ExpiryExchangeFunc<T, TArg>(
        T target, in TArg arg, out DateTime? oldExpiry);
}
