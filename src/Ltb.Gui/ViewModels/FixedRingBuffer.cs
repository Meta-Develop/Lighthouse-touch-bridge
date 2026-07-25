using System.Collections;

namespace Ltb.Gui.ViewModels;

/// <summary>
/// Fixed-capacity chronological storage used by the opt-in diagnostic plots.
/// The backing array never grows and disabling diagnostics clears all retained
/// samples.
/// </summary>
internal sealed class FixedRingBuffer<T> : IReadOnlyList<T>
{
    private readonly T[] _items;
    private int _start;

    public FixedRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

            return _items[(_start + index) % Capacity];
        }
    }

    public void Add(T item)
    {
        if (Count < Capacity)
        {
            _items[(_start + Count) % Capacity] = item;
            Count++;
            return;
        }

        _items[_start] = item;
        _start = (_start + 1) % Capacity;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
