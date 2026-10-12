namespace Rebellion.Core.Clock;

/// <summary>Exported generator state for saves. Draws is bookkeeping only; it does not affect the sequence.</summary>
public readonly record struct RngState(ulong State, ulong Increment, long Draws);

/// <summary>
/// The one game random generator: PCG32 XSH-RR (64-bit state, 64-bit odd increment), written here so results never
/// depend on the .NET version or platform. FROZEN: saves depend on it.
/// rule: m1-clock-and-orders §Seeded random numbers
///
/// Seeding procedure (identical to the reference pcg32_srandom_r(initstate = seed, initseq = stream)):
///   state = 0; inc = (stream &lt;&lt; 1) | 1; step(); state += seed; step().
/// The default stream is 54, so seed 42 starts 2707161783, 2068313097, 3122475824, ... (pinned in tests).
/// </summary>
public class GameRng
{
    public const ulong DefaultStream = 54;
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private ulong _inc;

    /// <summary>Number of NextUInt calls so far (every draw goes through NextUInt).</summary>
    public long Draws { get; private set; }

    public GameRng(ulong seed, ulong stream = DefaultStream)
    {
        _inc = (stream << 1) | 1UL;
        Step();
        _state += seed;
        Step();
    }

    private GameRng(RngState s) { _state = s.State; _inc = s.Increment; Draws = s.Draws; }

    private ulong Step()
    {
        ulong old = _state;
        _state = unchecked(old * Multiplier + _inc);
        return old;
    }

    public uint NextUInt()
    {
        ulong old = Step();
        Draws++;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform integer in [min, max], both inclusive, by rejection (no modulo bias).</summary>
    public virtual int NextInt(int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive) throw new ArgumentException("max must be >= min");
        ulong range = (ulong)((long)maxInclusive - minInclusive + 1);
        if (range == 1UL << 32) return unchecked((int)NextUInt());
        uint r = (uint)range;
        uint threshold = (uint)(-(int)r) % r; // 2^32 mod range: the lowest values are rejected
        uint x;
        do { x = NextUInt(); } while (x < threshold);
        return (int)(minInclusive + (long)(x % r));
    }

    public RngState Export() => new(_state, _inc, Draws);

    public static GameRng Import(RngState s) => new(s);
}
