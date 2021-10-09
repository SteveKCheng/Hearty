using System;
using System.Collections.Generic;
using GoldSaucer.BTree;
using System.Threading;

namespace JobBank.Server
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
        /// <returns>The due time for triggering the next timer
        /// event, in milliseconds.  This value is 0 if there
        /// are more items to expire now.  This value is <see cref="Timeout.Infinite" />
        /// if there are no more items to expire at all, until more
        /// requests come in.
        /// </returns>
        private int ExpireOneBatch(int count)
        {
            lock (_sortedEntries)
            {
                var now = DateTime.UtcNow;

                var enumerator = _sortedEntries.GetEnumerator(toBeginning: false);

                try
                {
                    while (enumerator.MovePrevious())
                    {
                        var entry = enumerator.Current.Key;

                        // Quit loop if the next item, and therefore all items afterward,
                        // have not yet expired
                        if (entry.Expiry > now)
                        {
                            var nextTimerExpiry = _earliestExpiry = BucketExpiryTime(entry.Expiry);
                            return GetDueTime(nextTimerExpiry, now);
                        }

                        // Pause processing if we already processed enough items in this run
                        if (count <= 0)
                        {
                            _earliestExpiry = BucketExpiryTime(enumerator.Current.Key.Expiry);
                            return 0;
                        }

                        --count;

                        enumerator.RemoveCurrent();
                        _expiryAction.Invoke(entry.Target);
                    }

                    // No more items, so do not trigger the timer until more are added.
                    _earliestExpiry = DateTime.MaxValue;
                    return Timeout.Infinite;
                }
                finally
                {
                    enumerator.Dispose();
                }
            }
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
        }

        /// <summary>
        /// Callback function for <see cref="_timer"/> to expire
        /// items from <see cref="_sortedEntries" />.
        /// </summary>
        private static void OnTimerFiring(object? state)
        {
            var self = (ExpiryQueue<T>)state!;
            int dueTime;
            do
            {
                dueTime = self.ExpireOneBatch(count: 10);
            } while (dueTime == 0);

            if (dueTime > 0)
                self._timer.Change(dueTime, period: Timeout.Infinite);
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
        /// <param name="oldExpiry">
        /// The old expiry time for the target item.  If the item was
        /// requested to be expired before, this argument must match the
        /// expiry time given then, so that the old expiration can be
        /// cancelled.  Set to null when adding a new item.
        /// </param>
        /// <param name="newExpiry">
        /// The new expiry time for the target item. Set to null
        /// to remove the item from being expired.
        /// </param>
        public DateTime? ChangeExpiry<TArg>(T target, in TArg arg, ExpiryExchangeFunc<T, TArg> exchangeFunc)
        {
            lock (_sortedEntries)
            {
                var newExpiry = exchangeFunc(target, arg, out var oldExpiry);
                if (newExpiry == oldExpiry)
                    return newExpiry;

                if (oldExpiry != null)
                    _sortedEntries.Remove(new Entry(target, oldExpiry.GetValueOrDefault()));

                if (newExpiry != null)
                {
                    _sortedEntries.Add(new Entry(target, newExpiry.GetValueOrDefault()), true);

                    var expiry = BucketExpiryTime(newExpiry.GetValueOrDefault());
                    if (expiry < _earliestExpiry)
                    {
                        _earliestExpiry = expiry;
                        int dueTime = GetDueTime(expiry, DateTime.Now);
                        _timer.Change(dueTime, period: Timeout.Infinite);
                    }
                }

                return newExpiry;
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
