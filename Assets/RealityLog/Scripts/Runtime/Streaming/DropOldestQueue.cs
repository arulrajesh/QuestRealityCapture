#nullable enable

using System;
using System.Collections.Generic;

namespace RealityLog.Streaming
{
    /// <summary>Thread-safe bounded FIFO. When full, the oldest item is dropped (and disposed via the callback).</summary>
    public sealed class DropOldestQueue<T>
    {
        private readonly Queue<T> items = new();
        private readonly int capacity;
        private readonly Action<T>? onDrop;
        private readonly object gate = new();

        public DropOldestQueue(int capacity, Action<T>? onDrop = null)
        {
            this.capacity = Math.Max(1, capacity);
            this.onDrop = onDrop;
        }

        public long Dropped { get; private set; }

        public int Count
        {
            get { lock (gate) { return items.Count; } }
        }

        public void Enqueue(T item)
        {
            T? dropped = default;
            var hasDropped = false;
            lock (gate)
            {
                if (items.Count >= capacity)
                {
                    dropped = items.Dequeue();
                    hasDropped = true;
                    Dropped++;
                }
                items.Enqueue(item);
            }
            if (hasDropped)
            {
                onDrop?.Invoke(dropped!);
            }
        }

        public bool TryDequeue(out T item)
        {
            lock (gate)
            {
                if (items.Count > 0)
                {
                    item = items.Dequeue();
                    return true;
                }
            }
            item = default!;
            return false;
        }

        public void Clear()
        {
            while (TryDequeue(out var item))
            {
                onDrop?.Invoke(item);
            }
        }
    }
}
