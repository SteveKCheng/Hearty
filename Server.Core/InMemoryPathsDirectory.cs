using Hearty.BTree;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Hearty.Server;

/// <summary>
/// Stores all the paths for promises in managed memory.
/// </summary>
public class InMemoryPathsDirectory : PathsDirectory
{
    private readonly BTreeMap<PromisePath, ulong> _btree;

    private readonly ReaderWriterLockSlim _btreeLock;

    public InMemoryPathsDirectory()
    {
        _btree = new BTreeMap<PromisePath, ulong>(32, Comparer<PromisePath>.Default);
        _btreeLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
    }

    /// <inheritdoc />
    public override void AddPath(ref PromisePath path, PromiseId id)
    {
        bool isAutoIncrement = string.IsNullOrEmpty(path.Name);

        _btreeLock.EnterWriteLock();
        try
        {
            if (isAutoIncrement)
            {
                var folderPath = PromisePath.ForFolder(path.Folder);
                bool folderExists = _btree.TryGetValue(folderPath, out var number);
                ++number;
                _btree[folderPath] = number;
                path = PromisePath.ForNumberedFile(path.Folder, (uint)number);
            }

            _btree.Add(path, id.RawInteger);
        }
        finally
        {
            _btreeLock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<KeyValuePair<PromisePath, PromiseId>>
        ListSubpaths(PromisePath startPath, int suggestedCount)
    {
        /*
        _btreeLock.EnterReadLock();
        try
        {
            // _btree.GetEnumerator()
        }
        finally
        {
            _btreeLock.ExitReadLock();
        }*/
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void RemovePath(PromisePath path, PromiseId id)
    {
        if (path.Name != null && path.Name.Length == 0)
            throw new ArgumentException("TryGetPath may not be called for a folder path. ", nameof(path));

        _btreeLock.EnterWriteLock();
        try
        {
            _btree.Remove(path);
        }
        finally
        {
            _btreeLock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public override bool TryGetPath(PromisePath path, out PromiseId id)
    {
        if (path.Name != null && path.Name.Length == 0)
            throw new ArgumentException("TryGetPath may not be called for a folder path. ", nameof(path));

        _btreeLock.EnterReadLock();
        try
        {
            bool success = _btree.TryGetValue(path, out ulong value);
            id = new PromiseId(value);
            return success;
        }
        finally
        {
            _btreeLock.ExitReadLock();
        }
    }
}
