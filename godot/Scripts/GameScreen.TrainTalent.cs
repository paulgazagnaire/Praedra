using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Missions;

namespace PraedraViewer;

/// <summary>
/// Train Talent on the screens (UI designer, specs/ui/train-talent-2026-11-25/design.md rev 3; Paul 2026-11-25): who trains whom in the leader
/// hover and the Leaders tab, why the planner refuses a team, and its step 3. The core decides (<see cref="CharacterModule.IsTrainer"/>,
/// <see cref="MissionModule.TrainerOf"/>, <see cref="MissionModule.StudentsOf"/>, <see cref="CharacterModule.TrainingOdds"/>); the words are
/// <see cref="TrainWords"/>. Only the player's own leaders are read. rule: m5-characters-and-talent §Rule (New) 4b, §Talent (Train Talent).
/// </summary>
public partial class GameScreen
{
    /// <summary>The player's trainers who are not captured or dead, the strongest first (ties to the lower id).</summary>
    List<CharacterRecord> MyTrainers() => S.MyCharacters.Where(c => c.Status is CharacterStatus.Free or CharacterStatus.Injured && Cm.IsTrainer(c))
        .OrderByDescending(c => c.EffectiveTalent).ThenBy(c => c.Id).ToList();

    static bool KnownTalent(CharacterRecord c) => c.TalentHolder && c.TalentDiscovered;

    /// <summary>What keeps a leader from joining a team now, in a few words for brackets, or null when they can go.</summary>
    string BusyWords(CharacterRecord c) =>
        c.Status == CharacterStatus.Injured ? "injured"
        : S.MissionOf(c.Id) != null ? "on a mission"
        : S.PendingMissionOf(c.Id) != null ? "ordered on a mission"
        : c.AttachedFleet != 0 ? "aboard a fleet"
        : S.SystemPostOf(c) != null ? "the General here"
        : c.Transit != null ? "about to travel"
        : CanJoin(c) ? null : "busy";

    /// <summary>The same as a sentence's end, with the way out where there is one (U5: a leader leaves a fleet by drag only).</summary>
    string BusySentence(CharacterRecord c) => BusyWords(c) switch
    {
        "aboard a fleet" => "is aboard a fleet (drag them onto the planet first)",
        "the General here" => "is the General here (release the post first)",
        string w => "is " + w,
        null => "is busy",
    };

    TrainWords.Who WhoOf(CharacterRecord c, string from)
    {
        string at = Cm.LocationOf(c);
        return new TrainWords.Who(S.CharName(c.Id), c.EffectiveTalent, at == from ? null : at ?? "on the move", BusyWords(c));
    }

    /// <summary>The talented leaders a trainer could train where it stands (aware, less Talent, not captured or dead), in id order.</summary>
    List<CharacterRecord> StudentsNear(CharacterRecord t)
    {
        string at = Cm.LocationOf(t);
        if (at == null) return new();
        return S.MyCharacters.Where(c => c.Id != t.Id && c.Status is CharacterStatus.Free or CharacterStatus.Injured && KnownTalent(c)
            && c.EffectiveTalent < t.EffectiveTalent && Cm.LocationOf(c) == at).OrderBy(c => c.Id).ToList();
    }

    /// <summary>The trainers a talented leader could learn from: more Talent, not captured or dead; those where it stands first, then by Talent.</summary>
    List<CharacterRecord> TrainersFor(CharacterRecord s)
    {
        string at = Cm.LocationOf(s);
        return MyTrainers().Where(t => t.Id != s.Id && t.EffectiveTalent > s.EffectiveTalent)
            .OrderBy(t => at != null && Cm.LocationOf(t) == at ? 0 : 1).ThenByDescending(t => t.EffectiveTalent).ThenBy(t => t.Id).ToList();
    }

    /// <summary>The Posts line's posts, in the order the sheet lists them.</summary>
    List<string> PostsOf(CharacterRecord c)
    {
        var cls = Cm.ClassOf(c);
        return new[] { cls.CanAdmiral ? "Admiral" : null, cls.CanCommander ? "Commander" : null, cls.CanGeneral ? "General" : null }.Where(x => x != null).ToList();
    }

    /// <summary>
    /// A leader's hover, one idea per line (design piece 1): name and skills, Talent, who they train or learn from, posts, the role, then what a
    /// drag and a right-click do. Shared by the leader tiles and the Fleet window's leader cards.
    /// </summary>
    string LeaderHover(CharacterRecord c, string role, bool canDrag, bool mission)
    {
        var lines = new List<string> { $"{S.CharTitle(c.Id)}. {S.SkillsOf(c, talent: false)}" };
        bool trainer = Cm.IsTrainer(c);
        if (KnownTalent(c) && Cm.RankOf(c) is TalentRank r) lines.Add(TrainWords.TalentLine(GameSession.RankWords(r), c.EffectiveTalent, trainer));
        if (c.Side == Me && KnownTalent(c))
        {
            if (trainer)
            {
                lines.Add(TrainWords.CanTrainHere(StudentsNear(c).Select(x => WhoOf(x, Cm.LocationOf(c))).ToList(), c.EffectiveTalent));
                if (Cm.Params.TalentDiscovery == TalentDiscoveryMode.Trainer) lines.Add(TrainWords.Wakes); // rule: §Talent (Talent discovery: trainers awaken)
            }
            if (TrainWords.CanLearnFrom(TrainersFor(c).Select(t => WhoOf(t, Cm.LocationOf(c))).ToList()) is string learn) lines.Add(learn);
        }
        var posts = PostsOf(c);
        lines.Add(TrainWords.Posts(posts));
        if (role != null) lines.Add(role + ".");
        if (canDrag) lines.Add(TrainWords.Drag(posts.Count > 0));
        if (mission) lines.Add(TrainWords.RightClick(TrainPartners(c.System, c.Id) != null));
        return Ui.Wrap(string.Join("\n", lines));
    }

    /// <summary>The Leaders tab's training line (design piece 4), or null for a leader who neither trains nor can learn.</summary>
    string TrainTabLine(CharacterRecord c)
    {
        if (c.Status is not (CharacterStatus.Free or CharacterStatus.Injured) || !KnownTalent(c)) return null;
        string at = Cm.LocationOf(c);
        if (Cm.IsTrainer(c)) return TrainWords.TabTrainer(StudentsNear(c).Select(x => WhoOf(x, at)).ToList());
        return TrainersFor(c).FirstOrDefault() is { } t ? TrainWords.TabStudent(WhoOf(t, at)) : null;
    }

    /// <summary>
    /// Why the planner's team cannot train (design pieces 2 and 3), or null when it can. Checked before the core's dry run so the reason names
    /// who is missing and where they are; the core still judges the order. rule: m5-characters-and-talent §Talent (Train Talent: the team).
    /// </summary>
    string TrainRefusal()
    {
        string home = _plan.Home;
        if (_plan.Sfs.Count > 0) return TrainWords.OnlyLeaders;
        if (S.OwnerOf(home) != Me) return TrainWords.NotHeld;
        var team = _plan.Chars.ToList();
        var trainers = MyTrainers();
        if (Mm.TrainerOf(team) is not { } t)
        {
            var here = trainers.Where(x => Cm.LocationOf(x) == home).ToList();
            if (here.Where(CanJoin).Select(x => S.CharName(x.Id)).ToList() is { Count: > 0 } free) return TrainWords.NeedTrainerInTeam(free);
            if (here.FirstOrDefault() is { } busy) return TrainWords.TrainerBusy(S.CharName(busy.Id), BusySentence(busy));
            if (trainers.Count == 0) return TrainWords.NoTrainerFree;
            return TrainWords.NeedTrainerHere(trainers.Select(x => S.CharName(x.Id)));
        }
        if (Mm.StudentsOf(team, t).Count > 0) return null;
        var others = team.Where(id => id != t.Id).Select(id => Cm.Get(id)).OfType<CharacterRecord>().ToList();
        if (others.FirstOrDefault(x => !KnownTalent(x)) is { } plain) return TrainWords.NoKnownTalent(S.CharName(plain.Id));
        if (others.FirstOrDefault(x => x.EffectiveTalent >= t.EffectiveTalent) is { } strong) return TrainWords.NotBelow(S.CharName(t.Id), WhoOf(strong, home));
        var near = StudentsNear(t).Where(x => !_plan.Chars.Contains(x.Id)).ToList();
        if (near.Any(CanJoin)) return TrainWords.TickStudent;
        // the team's trainer is the student of a stronger one standing here (Cassia under The Chair, CH42)
        if (trainers.Where(x => x.EffectiveTalent > t.EffectiveTalent && Cm.LocationOf(x) == home && CanJoin(x) && !_plan.Chars.Contains(x.Id))
                .Select(x => S.CharName(x.Id)).ToList() is { Count: > 0 } stronger) return TrainWords.NeedTrainerInTeam(stronger);
        if (near.FirstOrDefault() is { } away) return TrainWords.StudentBusy(S.CharName(away.Id), BusySentence(away));
        return TrainWords.NoStudentHere;
    }

    /// <summary>The planner's step 3 for Train Talent (design piece 3): no map pick, the team trains where it stands.</summary>
    void TrainingStep(MissionPlan p)
    {
        Heading(TrainWords.StepHeading);
        p.Target = p.Home; p.TargetChar = null;
        var team = p.Chars.ToList();
        if (Mm.TrainerOf(team) is { } t)
        {
            Line(TrainWords.TrainerStep(WhoOf(t, p.Home), p.Home), Good, 13);
            foreach (var s in Mm.StudentsOf(team, t))
            {
                var (chance, most) = Cm.TrainingOdds(t.EffectiveTalent - s.EffectiveTalent);
                Line(TrainWords.StudentStep(WhoOf(s, p.Home), chance, most, t.EffectiveTalent), null, 13);
            }
        }
        var rec = Mm.Data.Get(MissionKind.TrainTalent);
        Line(TrainWords.Lasts(rec.TimerMin, rec.TimerMin + rec.TimerSpread), Dim, 13);
        if (Cm.Params.TalentDiscovery == TalentDiscoveryMode.Trainer) Line(TrainWords.NoWaking, Dim, 13); // rule: §Talent (a trainer on a mission awakens nobody)
        Line(TrainWords.StopsIf(p.Home), Dim, 13);
        var chk = Mm.CheckStart(PlanOrder(MissionKind.TrainTalent, p.Home, null));
        if (chk.Reason != null) Line($"{p.Home}: {GameSession.WhyMission(chk.Reason)}.", Bad);
        Add(new Control { CustomMinimumSize = new Vector2(0, 4) });
        Act($"Send: {GameSession.KindWords(MissionKind.TrainTalent)} on {p.Home}", ConfirmMission, "Starts as soon as the clock moves. The core judges the order then; a refusal comes back in the Log (E).", chk.Reason != null);
    }
}
