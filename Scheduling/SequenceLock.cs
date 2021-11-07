using System;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace JobBank.Scheduling
{
    /// <summary>
    /// A piece of published data protected by a "sequence lock".
    /// </summary>
    /// <remarks>
    /// "Sequence lock" are not really locks but a form of 
    /// transactional memory, made well-known by their usage
    /// in the Linux kernel. They are ideal for publishing small
    /// amounts of data for concurrent readers without taking full
    /// locks.
    /// </remarks>
    /// <typeparam name="T">
    /// The data to publish, expected to be a structure.
    /// </typeparam>
    internal struct SequenceLock<T>
    {
        /// <summary>
        /// The data being protected by this sequence lock.
        /// </summary>
        private T _data;

        /// <summary>
        /// The sequence number for this sequence lock.
        /// </summary>
        private uint _version;

        /// <summary>
        /// Read the most recent snapshot of the protected data, 
        /// without tearing.
        /// </summary>
        public T Read()
        {
            var spinWait = new SpinWait();
            do
            {
                uint version1 = Volatile.Read(ref _version);

                var data = _data;

                // This needs to be a load-load barrier but .NET does not have any
                // API for that.  Assume the machine code generator is not doing
                // any clever re-ordering of these loads on x86.  Use a full
                // memory fence if we are running on non-x86 hardware, which
                // may sport weakly-ordered memory access.
                if (!X86Base.IsSupported)
                    Thread.MemoryBarrier();

                uint version2 = Volatile.Read(ref _version);

                if (version1 == version2 && (version1 & 1u) == 0)
                    return data;

                spinWait.SpinOnce();
            } while (true);
        }

        /// <summary>
        /// Prepare to update the protected data.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only one thread may update at a time. 
        /// If concurrent threads call this method, they will spin
        /// until the concurrency is resolved.  You probably want to
        /// avoid concurrent writers, but they will work correctly.
        /// </para>
        /// <para>
        /// You must call <see cref="EndWriteTransaction(uint, T)" />
        /// unconditionally after this method returns, or the sequence
        /// lock will be "stuck".  Be especially careful of exceptions.
        /// </para>
        /// </remarks>
        /// <param name="version">
        /// The sequence number for the current snapshot,
        /// required to complete the transaction.
        /// </param>
        /// <returns>The current snapshot of the protected data,
        /// from which updates may be computed by the caller.  </returns>
        public T BeginWriteTransaction(out uint version)
        {
            var spinWait = new SpinWait();
            do
            {
                var oldVersion = Interlocked.Or(ref _version, 1u);

                if ((oldVersion & 1u) == 0)
                {
                    // The above "interlocked" operation in .NET implies a full memory
                    // barrier in .NET and so it already prevents any following store to
                    // _data from being re-ordered to before the store to _version.
                    version = oldVersion;
                    return _data;
                }

                spinWait.SpinOnce();
            } while (true);
        }

        /// <summary>
        /// Finish updating the protected data.
        /// </summary>
        /// <param name="version">
        /// The exact version number that had been provided by
        /// <see cref="BeginWriteTransaction(out uint)" /> on return.
        /// </param>
        /// <param name="data">
        /// The data to set as the new current version.
        /// </param>
        public void EndWriteTransaction(uint version, in T data)
        {
            _data = data;
            Volatile.Write(ref _version, unchecked(version + 2));
        }

        /// <summary>
        /// Abandon an outstanding write transaction without updating
        /// the protected data.
        /// </summary>
        /// <param name="version">
        /// The exact version number that had been provided by
        /// <see cref="BeginWriteTransaction(out uint)" /> on return.
        /// </param>
        public void EndWriteTransaction(uint version)
        {
            Volatile.Write(ref _version, unchecked(version + 2));
        }
    }
}
