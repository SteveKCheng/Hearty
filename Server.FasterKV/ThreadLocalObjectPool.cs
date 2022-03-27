using System;
using System.Collections.Generic;
using System.Threading;

namespace Hearty.Server.FasterKV;

internal interface IThreadLocalObjectPoolHooks<T, THooks>
    where T : IDisposable
    where THooks : IThreadLocalObjectPoolHooks<T, THooks>
{
    T InstantiateObject();

    ref ThreadLocalObjectPool<T, THooks> Root { get; }
}

internal struct ThreadLocalObjectPool<T, THooks>
    where T : IDisposable
    where THooks : IThreadLocalObjectPoolHooks<T, THooks>
{
    private readonly THooks _hooks;
    private readonly LinkedList<T> _linkedList;

    public ThreadLocalObjectPool(THooks hooks)
    {
        _hooks = hooks;
        _linkedList = new LinkedList<T>();
        _forCurrentThread = new ThreadLocal<LinkedListNode<T>?>(trackAllValues: false);
    }

    public struct Use : IDisposable
    {
        private readonly IThreadLocalObjectPoolHooks<T, THooks> _hooks;
        private readonly LinkedListNode<T>? _node;

        internal Use(IThreadLocalObjectPoolHooks<T, THooks> hooks,
                     T target)
        {
            _hooks = hooks;
            _node = null;
            Target = target;
        }

        internal Use(IThreadLocalObjectPoolHooks<T, THooks> hooks,
                     LinkedListNode<T> node)
        {
            _hooks = hooks;
            _node = node;
            Target = node.Value;
        }

        public T Target { get; }

        public void Dispose()
        {
            var root = _hooks.Root;

            var node = _node;
            if (node is null)
            {
                node = new LinkedListNode<T>(Target);
                lock (root._linkedList)
                    root._linkedList.AddLast(node);
            }

            root._forCurrentThread.Value = node;
        }
    }

    private readonly ThreadLocal<LinkedListNode<T>?> _forCurrentThread;

    public Use GetForCurrentThread()
    {
        var node = _forCurrentThread.Value;

        if (node is not null)
        {
            _forCurrentThread.Value = null;
            return new Use(_hooks, node);
        }
        else
        {
            T target = _hooks.InstantiateObject();
            return new Use(_hooks, target);
        }
    }

    public void Dispose()
    {
        _forCurrentThread.Dispose();

        lock (_linkedList)
        {
            foreach (var node in _linkedList)
                node.Dispose();
        }
    }
}
