using Rebellion.Core.Clock;

namespace Rebellion.Core.Fog;

/// <summary>A group value with its stamp and age (age null in Classic). rule: m5-fog-and-hq §N2.</summary>
public sealed record Seen<T>(T Value, int Stamp, int? AgeDays);

/// <summary>
/// A read-only look at one explored picture, with ages on day <c>Day</c>. A group never written is null. rule: m5-fog-and-hq §N2, §N8 Systems.
/// </summary>
public sealed class SystemPicture
{
    private readonly SystemKnowledge _k;
    private readonly int _day;
    private readonly bool _ages;

    internal SystemPicture(string system, SystemKnowledge k, int day, bool ages) { System = system; _k = k; _day = day; _ages = ages; }

    public string System { get; }
    public int? LastLookDay => _k.LastLookDay;
    private Seen<T>? G<T>(int? stamp, T v) => stamp is int s ? new Seen<T>(v, s, _ages ? _day - s : null) : null;

    /// <summary>Group A: populated, energy and raw slots.</summary>
    public Seen<(bool Populated, int Energy, int Raw)>? Status => G(_k.StampA, (_k.Populated, _k.Energy, _k.Raw));
    /// <summary>Group B: holder (null = neutral) and Alliance support.</summary>
    public Seen<(Side? Owner, int AllianceSupport)>? Owner => G(_k.StampB, (_k.Owner, _k.AllianceSupport));
    /// <summary>Group C: completed facilities by class.</summary>
    public Seen<IReadOnlyDictionary<string, int>>? Facilities => G<IReadOnlyDictionary<string, int>>(_k.StampC, _k.Facilities);
    public bool HqHere => _k.HqHere;
    /// <summary>Group D: ground forces by side and class.</summary>
    public Seen<IReadOnlyList<GroundPicture>>? Ground => G<IReadOnlyList<GroundPicture>>(_k.StampD, _k.Ground);
    /// <summary>Group E: the other side's fleets at the last look (class counts only).</summary>
    public Seen<IReadOnlyList<FleetPicture>>? Fleets => G<IReadOnlyList<FleetPicture>>(_k.StampE, _k.Fleets);
}

/// <summary>
/// Everything one side may read (the fairness contract): the day, its own pictures with levels and ages, its sighting log, its HQ belief and
/// clues, and its filtered event feed. The truth world, the other side's ids, orders and the HQ state are not reachable from here.
/// Own-side state (own fleets, pools, the Alliance's own HQ) is read from the game as before: it is the side's own. rule: m5-fog-and-hq §N8.
/// </summary>
public sealed class KnowledgeView
{
    private readonly FogModule _fog;
    private readonly Game _game;

    internal KnowledgeView(FogModule fog, Side side, Game game) { _fog = fog; Side = side; _game = game; }

    public Side Side { get; }
    public int Day => _game.Day;
    public int ObservedWindowDays => _fog.Params.ObservedWindowDays;

    public KnowledgeLevel Level(string system) => _fog.Level(Side, system);
    public bool Sees(string system) => _fog.Sees(Side, system);
    public bool IsExplored(string system) => _fog.IsExplored(Side, system);
    public SystemPicture? Picture(string system) => _fog.Picture(Side, system);
    public IReadOnlyList<(string System, FleetPicture Fleet)> VisibleFleets() => _fog.VisibleFleets(Side);
    public KnownHqInfo? KnownHq => _fog.KnownHq(Side);
    public IReadOnlyList<HqSectorClue> HqSectorClues => _fog.Clues(Side);
    public IReadOnlyList<Sighting> Sightings(int sinceDay = 0) => _fog.Sightings(Side, sinceDay);
    /// <summary>The events this side may see (§N8 audience), in order.</summary>
    public IReadOnlyList<GameEvent> Events => _fog.EventsFor(Side);
    public string Hash() => _fog.KnowledgeHash(Side);
}
