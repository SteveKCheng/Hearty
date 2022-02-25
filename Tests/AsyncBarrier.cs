using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hearty.Tests
{
    /// <summary>
    /// Asynchronous version of <see cref="Barrier" /> with a simplistic
    /// implementation.
    /// </summary>
    internal class AsyncBarrier
    {
        private readonly TaskCompletionSource _taskSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public AsyncBarrier(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            _count = count;
        }

        public Task SignalAndWaitAsync()
        {
            // Decrement _count, flooring at zero
            int c, d;
            do
            {
                c = _count;
                if (c <= 0)
                {
                    throw new InvalidOperationException(
                        "Too many participants are calling AsyncBarrier.SignalAndWaitAsync. ");
                }

                d = c - 1;
            } while (Interlocked.CompareExchange(ref _count, d, c) != c);

            if (d == 0)
                _taskSource.SetResult();

            return _taskSource.Task;
        }
    }
}
