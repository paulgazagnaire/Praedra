namespace Praedra.Core.Clock;

/// <summary>
/// A read-only list with value equality, so event records that carry lists (paths, fleet ids) compare equal when their
/// contents are equal. Used in events only; never part of a save.
/// </summary>
public sealed record ValueList<T>(IReadOnlyList<T> Items)
{
    public bool Equals(ValueList<T>? other) => other != null && Items.SequenceEqual(other.Items);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            foreach (var i in Items) h = h * 31 + (i?.GetHashCode() ?? 0);
            return h;
        }
    }

    public override string ToString() => string.Join(", ", Items);
}
