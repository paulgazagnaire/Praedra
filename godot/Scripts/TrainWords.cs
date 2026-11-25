using System.Collections.Generic;
using System.Linq;

namespace PraedraViewer;

/// <summary>
/// Every Train Talent line on the screens (UI designer, specs/ui/train-talent-2026-11-25/design.md rev 3, with Paul's answers of 2026-11-25).
/// A trainer's name always comes after a colon, a comma, "or" or "and", or inside brackets, or is followed by its Talent in brackets; it is never
/// the bare subject or object of a sentence, because "Nobody" is a Collective trainer and "needs Nobody" reads as "needs no one" (review 28).
/// The playtest builds every line with the Collective's trainers and checks that (<see cref="BareTrainer"/>). The names come from the side's
/// trainer list in data, never from this file. rule: m5-characters-and-talent §Rule (New) 4b, §Talent (Train Talent: the roll, the team).
/// </summary>
static class TrainWords
{
    /// <summary>A leader in a line: name, Talent, where they stand (null = here) and what keeps them from going now (null = free to go).</summary>
    public readonly record struct Who(string Name, int Talent, string Where = null, string Busy = null);

    static string Short(Who w) => $"{w.Name} ({w.Talent}{(w.Busy != null ? ", " + w.Busy : "")})";
    static string Placed(Who w) => Short(w) + (w.Where == null ? " here" : $" at {w.Where}");
    static string Or(IEnumerable<string> names) => string.Join(" or ", names);

    /// <summary>Up to two leaders by name, then "and N more" (design piece 1).</summary>
    static string Few(IReadOnlyList<Who> list) => list.Count switch
    {
        1 => Short(list[0]),
        2 => $"{Short(list[0])} and {Short(list[1])}",
        _ => $"{Short(list[0])} and {list.Count - 1} more",
    };

    // ---- the leader hover (design piece 1)
    public static string TalentLine(string rank, int talent, bool trainer) => $"Talent: {rank} ({talent})." + (trainer ? " Trains Talent." : "");
    public static string CanTrainHere(IReadOnlyList<Who> students, int trainerTalent) => students.Count == 0
        ? $"Can train your talented leaders below {trainerTalent}. None is here."
        : $"Can train here: {Few(students)}.";
    public const string Wakes = "Each day, wakes hidden Talent in your leaders on the same planet or fleet.";
    public static string CanLearnFrom(IReadOnlyList<Who> trainers) => trainers.Count == 0 ? null : "Can learn from: " + string.Join(", ", trainers.Select(Placed)) + ".";
    public static string Posts(IReadOnlyList<string> posts) => posts.Count == 0 ? "Posts: none." : $"Posts: {string.Join(", ", posts)}.";
    public static string Drag(bool hasPost) => hasPost ? "Drag onto a post, or onto a fleet to ride it." : "Drag onto a fleet to ride it.";
    public static string RightClick(bool train) => train ? "Right-click: Train Talent and other missions." : "Right-click: missions.";

    // ---- the Leaders tab (design piece 4)
    public static string TabTrainer(IReadOnlyList<Who> students) => students.Count == 0 ? "Trains Talent: no student here." : $"Trains Talent: {Few(students)} here.";
    public static string TabStudent(Who trainer) => $"Learns from: {Placed(trainer)}.";

    // ---- the planner's refusals for Train Talent (design pieces 2 and 3; shown as "Train Talent - reason")
    public const string OnlyLeaders = "only leaders train";
    public const string NotHeld = "only on a planet you hold";
    public const string NoTrainerFree = "no trainer is free";
    public const string TickStudent = "tick a student in the team";
    public const string NoStudentHere = "no talented leader with less Talent is here";
    public static string NeedTrainerInTeam(IEnumerable<string> trainers) => $"needs a trainer in the team ({Or(trainers)})";
    public static string NeedTrainerHere(IEnumerable<string> trainers) => $"needs a trainer on this planet ({Or(trainers)})";
    public static string TrainerBusy(string trainer, string busy) => $"the trainer ({trainer}) {busy}";
    public static string StudentBusy(string student, string busy) => $"{student} {busy}";
    public static string NoKnownTalent(string leader) => $"{leader} has no known Talent";
    public static string NotBelow(string trainer, Who student) => $"the trainer ({trainer}) has no more Talent than {Short(student)}";

    // ---- the planner's step 3 (design piece 3)
    public const string StepHeading = "3. Training";
    public static string TrainerStep(Who trainer, string home) => $"Trainer: {trainer.Name} (Talent {trainer.Talent}). Trains on {home}, where the team stands: no travel.";
    public static string StudentStep(Who student, int chance, int most, int trainerTalent) => chance <= 0 || most <= 0
        ? $"{student.Name} (Talent {student.Talent}): no gain possible (the gap is too small)."
        : $"{student.Name} (Talent {student.Talent}): {chance}% chance to gain up to {most}, never reaching {trainerTalent}.";
    public static string Lasts(int min, int max) => $"Lasts {min} to {max} days. The team stays on the mission all that time: no posts, no travel. Each student gets one chance, at the end.";
    public const string NoWaking = "Meanwhile the trainer wakes no hidden Talent.";
    public static string StopsIf(string home) => $"Stops if you lose {home}.";

    // ---- the news
    public static string Grew(string student, int now, string trainer) => $"Train Talent: {student} grew in Talent, now {now} (trainer: {trainer}).";
    public static string NoProgress(string student, string trainer) => $"Train Talent: {student} made no progress this time (trainer: {trainer}).";

    /// <summary>Every line above with these two trainers and a student who is not a trainer, for the playtest's check (review 28).</summary>
    public static IEnumerable<string> AllLines(string a, string b, string student)
    {
        var ta = new Who(a, 150, null, null); var tb = new Who(b, 120, "Pronoia", "on a mission"); var st = new Who(student, 90);
        yield return CanLearnFrom(new[] { ta, tb });
        yield return TabStudent(ta); yield return TabStudent(tb);
        yield return NeedTrainerInTeam(new[] { a, b }); yield return NeedTrainerHere(new[] { a, b });
        yield return TrainerBusy(a, "is on a mission"); yield return NotBelow(a, st);
        yield return TrainerStep(ta, "Charter");
        yield return Grew(student, 100, a); yield return NoProgress(student, a);
        // a trainer can be the student of the stronger one (CH42)
        yield return CanTrainHere(new[] { tb with { Where = null, Busy = null } }, 150); yield return TabTrainer(new[] { tb with { Where = null, Busy = null }, st });
        yield return StudentStep(tb, 30, 15, 150); yield return Grew(b, 135, a); yield return NoProgress(b, a);
    }

    /// <summary>The first trainer name standing bare in the line (not after a colon, comma, "or", "and" or an opening bracket, and not followed by
    /// its Talent in brackets), or null when every one is framed.</summary>
    public static string BareTrainer(string line, IEnumerable<string> trainers)
    {
        foreach (var n in trainers)
            for (int i = line.IndexOf(n, System.StringComparison.Ordinal); i >= 0; i = line.IndexOf(n, i + n.Length, System.StringComparison.Ordinal))
            {
                string before = line[..i], after = line[(i + n.Length)..];
                bool framed = before.EndsWith('(') || before.EndsWith(": ") || before.EndsWith(", ") || before.EndsWith(" or ") || before.EndsWith(" and ")
                    || after.StartsWith(" (", System.StringComparison.Ordinal);
                if (!framed) return n;
            }
        return null;
    }
}
