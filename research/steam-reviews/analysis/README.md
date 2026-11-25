# Steam reviews: LOGISTICS, ECONOMY, MILITARY, COMBAT

Topic analysis of 743,591 English Steam reviews of 22 strategy/4X games (crawl finished 2026-11-25;
`ids.json` maps each key to its Steam appid and name). No new crawling was done. Raw data is NOT in
this folder; it stays on Paul's machine in `~/Repos/steam-reviews/raw/` (gzip copies in `archive/`).

Reproduce: `python3 scripts/topic_report.py` (stdlib only, one streaming pass, ~4.5 min).
`python3 scripts/topics.py spotcheck` writes 20 random matches per subtheme for precision checks.

## Files

| file | content |
|---|---|
| `topic_stats.csv` | one row per game x topic (`subtheme=ALL`) and game x subtheme, plus a `LATE_GAME` row per game |
| `topic_pooled.csv` | same rows pooled over all games, each game weighted equally |
| `phrases_<TOPIC>.csv` | bigrams/trigrams separating negative from positive reviews within the topic |
| `quotes_<TOPIC>.md` | per subtheme, the 6 most-upvoted negative and 6 most-upvoted positive snippets |
| `_run.json` | review counts of the run |

## Method

1. **Tagging** (`scripts/topics.py`). Each review is matched against case-insensitive, word-bounded
   regexes per subtheme (lists below). Before matching, known false-positive phrases are blanked out
   (globally: "pops up", "production values", "trade-off", "trading cards"; per subtheme: e.g. "end
   credits", "gold standard", "collecting dust", "game designer", "tactical nukes", "DLC prices",
   "means of production", "pursue victory"). Bare "cards" counts as auto-resolve only in Endless Space
   1 and 2, whose battles are resolved with battle-tactic cards. A review belongs to a topic if any of
   its subthemes matches. "Late game" (`late game|endgame|end game|mid game|midgame`) is tagged
   separately for co-occurrence.
2. **Precision check.** Two rounds of 20 random matches per subtheme. Round 1 found heavy noise in
   bare `counter(s)` (play counters, "to counter the bad reviews"), `pursue`/`chase`/`flee` ("pursue
   victory", "fled to Argentina"), `pricing` (DLC prices), `fleet power`, `AI manages to`,
   `automatically`; those were narrowed to mechanic contexts. Round 2 read ~80-95% on-topic for most
   subthemes. Remaining known noise: `attrition` ("war of attrition" as a victory style), `tactical`
   used loosely, `upkeep`/`maintenance` of mods or launchers, `gold` as a jokey award, `tedious` and
   `chore` (spec'd for micromanagement but generic).
3. **Stats** (`topic_stats.csv`). n matches; % of the game's reviews; % recommending among matches vs
   the game's overall %; `lift_pts` = difference in points; **`pct_recommend_expected_len` /
   `lift_len_adj_pts`** = the recommend rate expected from the matches' game and review length alone
   (word-count buckets <20, 20-49, 50-149, 150-399, 400+) and the difference from it; median playtime
   of matches vs the game; % of matches that also mention late game.
4. **Pooling** (`topic_pooled.csv`). Every game weighted equally: rates are the mean of per-game
   rates over games with at least 30 matches (`n_games_rated`); `pct_of_reviews` is the mean share
   over all 22 games; playtimes are the median of per-game medians. `pct_recommend_matches_unweighted`
   is the raw pooled rate (dominated by Civ 5/6, HOI4, Stellaris) for comparison.
5. **Phrases** (`phrases_<TOPIC>.csv`). Within the reviews of one topic, negative vs positive.
   Tokens: lowercase words, apostrophes dropped ("don't" -> "dont"), BBCode stripped, English
   stopwords removed but negations kept ("not", "no", "never", "dont", "cant", "without", "but").
   N-grams are built inside sentences, counted once per review (document frequency). Score: log-odds
   ratio with informative Dirichlet prior (Monroe, Colaresi & Quinn 2008), prior alpha_w = combined
   neg+pos count of w within the topic, z-score = delta / sqrt(variance); minimum 30 reviews.
   `NEGATIVE`/`POSITIVE` = the top 60 each way as asked. Those lists are dominated by verdict phrases
   ("not worth", "highly recommend") and game names, so `NEGATIVE_TOPICAL`/`POSITIVE_TOPICAL` repeat
   the ranking with any n-gram containing a verdict word or a game/series name removed.
6. **Quotes** (`quotes_<TOPIC>.md`). Per subtheme and side, the most-upvoted (`votes_up`) matching
   reviews; only the 1-2 sentences holding the first keyword match, max 300 chars; duplicate
   snippets skipped. No usernames are stored in the crawl.

## Caveats

- **Length confound (biggest one).** Recommend rate falls with review length: 92.5% for reviews
  under 20 words, 84.4% at 20-49, 74.2% at 50-149, 66.0% at 150-399, 60.2% at 400+. Keyword matches
  are mostly long reviews, so raw `lift_pts` is negative almost everywhere. Compare subthemes with
  `lift_len_adj_pts`, not `lift_pts`.
- **Playtime is `playtime_forever` at crawl time**, not playtime at review: the crawl did not store
  `playtime_at_review`. Read "median playtime" as how much the reviewer has played by now.
- **Small samples.** Only ~5% of reviews mention any of the four topics (most reviews are a few
  words). Several subthemes have fewer than 30 matches in most games (`n_games_rated` 2-4 for
  numbers_win, counters_rps, supply, fuel_range, chokepoints_lanes, auto_resolve); their pooled
  rates rest on those few games.
- **Mention is not cause.** A negative review that mentions supply may be negative for another
  reason. Quotes show what people say; the lifts show association only.
- **Keyword recall is partial.** Paraphrases ("ships run out of gas") are missed; game-specific words
  (alloys, influence, spice, IC) are mostly not in the lists.
- **Game mix.** Civ 5/6, HOI4, Stellaris, EU4 hold most reviews; the equal-weight pooling corrects
  their volume but not that "supply", "manpower" and "fuel" are mainly HOI4/EU4/DW vocabulary.
- English reviews only; Steam's helpfulness votes favor older reviews.

## Keyword lists

Each pattern is wrapped as `\b(?:pattern)\b`, case-insensitive. Exclusions are blanked first.

Global exclusions: `\b(?:pops? up|popping up|production values?|production quality|trade[- ]offs?|trading cards?|steam cards?|card drops?)\b`

### LOGISTICS

| subtheme | include | exclude (blanked first) |
|---|---|---|
| supply | `supply lines?\|supply range\|supply limits?\|supply system\|supply depots?\|supply chains?\|out of supply\|attrition` | — |
| transport | `freighters?\|convoys?\|cargo\|shipping lanes?\|hauling\|transport ships?\|troop transports?` | — |
| fuel_range | `fuel\|refuel(?:s\|ing\|ed\|led\|ling)?\|fuel range\|range limits?` | `fuel(?:ed\|led\|s)? (?:by\|my\|the\|your\|his\|her\|their\|this\|you\|me\|us) \w+\|add(?:s\|ing)? fuel\|fuel (?:to\|for) the fire\|as fuel` |
| trade_routes | `trade routes?` | — |
| chokepoints_lanes | `choke ?points?\|hyper ?lanes?\|star ?lanes?\|phase lanes?\|warp lanes?` | — |
| logistics | `logistics?\|logistical(?:ly)?` | — |

### ECONOMY

| subtheme | include | exclude (blanked first) |
|---|---|---|
| resources | `resources?\|minerals?\|strategic resources?\|luxury resources?\|luxur(?:y\|ies)` | `resource[- ](?:hog\|hungry\|intensive\|heavy)\|(?:system\|pc\|computer\|cpu\|gpu\|ram) resources?\|resources? (?:on\|of) (?:my\|your\|the) (?:pc\|computer\|system)\|(?:online\|external\|learning\|community) resources?\|luxury of` |
| income_money | `income\|credits\|gold\|dust\|tax(?:es\|ed\|ation)?` | `end credits\|credits roll\w*\|(?:collect\|gather\|bit)(?:s\|ing\|e\|es)? (?:the )?dust\|bites? the dust\|gold (?:edition\|version)\|worth (?:its\|it's\|it's) weight in gold\|pure gold\|gold ?mine\|golden\|gold standard\|(?:thanks?\|thx) (?:for\|4) (?:the )?gold\|games? with gold\|(?:the )?dust (?:has \|have )?settle\w*` |
| upkeep_deficit | `upkeep\|maintenance\|deficits?\|debts?\|bankrupt\w*` | `maintenance (?:mode\|patch\|update\|release)s?\|(?:server\|scheduled) maintenance` |
| market_trade | `markets?\|trading\|trade\|(?:market\|goods?\|commodity\|resource\|trade\|buy\|sell) prices` | `(?:on\|in) the market\|market(?:ing)? (?:for\|of) (?:4x\|strategy\|games?)\|(?:dlc\|game\|steam\|sale\|full\|these\|current) prices\|pricing (?:model\|of the dlc)\|marketing\|trade routes?\|horse ?trading` |
| micromanagement | `micro ?-?manag\w*\|tedious\|tedium\|chores?\|busy ?work\|spreadsheets?\|click(?:s\|ing) a lot` | — |
| automation | `automat(?:ion\|ed\|e\|es\|ing\|ions)\|governors?\|ai[- ]manag(?:ed\|ement)\|let(?:s\|ting)? (?:the )?ai manage\|sectors? ai\|auto ?-?manage\w*` | — |
| planet_city_mgmt | `planet(?:ary)? management\|city management\|colony management\|districts?\|jobs\|buildings\|pops` | `(?:great\|good\|nice\|amazing\|excellent\|awesome\|fantastic) jobs` |

### MILITARY

| subtheme | include | exclude (blanked first) |
|---|---|---|
| fleet_caps | `doom ?-?stacks?\|death ?-?stacks?\|stacks? of doom\|fleet cap\w*\|naval capacity\|naval cap\|command limits?\|unit caps?\|unit stack\w*\|stack(?:s\|ing)? (?:of\|your\|their) (?:units\|ships\|armies\|army\|fleets?)\|1 ?upt\|one unit per tile\|stack\w* units` | — |
| production | `build queues?\|production queues?\|production\|factor(?:y\|ies)\|shipyards?\|build times?` | `production (?:company\|studio\|team\|budget)\|(?:game\|tv\|film\|movie) production\|post[- ]production\|in production\|(?:dlc\|content) production\|production line of dlc\|means of production` |
| manpower | `manpower\|recruit\w*\|conscript\w*` | `recruit\w* (?:new \|more \|some )?(?:friends\|players\|people\|members)` |
| ship_unit_design | `ship (?:design(?:er\|s\|ing)?\|builder\|editor\|customi[sz]ation)\|unit design\w*\|designer\|loadouts?\|custom ships?\|design(?:ing)? (?:your\|my) (?:own )?ships` | `(?:game\|level\|lead\|sound\|ui\|narrative\|original\|the\|its\|their\|these\|those) designers?\|designers? (?:of\|at\|behind\|are\|have\|who\|that\|clearly\|really\|seem)` |
| army_invasion | `invasions?\|invad\w*\|ground combat\|ground battles?\|troops\|garrisons?\|armies\|ground forces` | — |
| ai_military | `ai (?:fleets?\|armies\|army\|navy\|navies\|ships\|units\|military)\|ai (?:doesn'?t\|does not\|never\|won'?t\|will not\|can'?t\|cannot\|rarely) (?:\w+ )?(?:attack\|invade\|declare war\|fight)\w*\|ai cheat\w*\|(?:enemy\|the) ai (?:attacks?\|invades?\|declares war)` | — |

### COMBAT

| subtheme | include | exclude (blanked first) |
|---|---|---|
| tactical_battles | `tactical\|real[- ]?time battles?\|battle ?maps?\|positioning\|formations?\|tactical (?:combat\|battles?)` | `tactical (?:pause\|nukes?\|nuclear\|bombs?\|missiles?\|advantage)\|(?:city\|settlement\|starting) positioning` |
| auto_resolve | `auto ?-?resolv\w*\|auto ?-?battles?\|auto ?-?combat\|battle cards?\|battle tactics? cards?\|cards` | — |
| counters_rps | `rock[- ]paper[- ]scissors?\|hard[- ]counter\w*\|soft[- ]counter\w*\|counter[- ]?play\|weapon triangle\|counter (?:system\|mechanics?\|units?\|ships?\|weapons?)\|counters? (?:to\|for\|against) (?:\w+ ){0,2}(?:units?\|ships?\|weapons?\|armou?r\|shields?\|every\w*)\|(?:units?\|ships?\|weapons?\|designs?\|types?) (?:that \|which )?counter(?:s\|ed)?\|counter(?:s\|ed\|ing)? (?:the \|an \|enemy \|their \|your \|each other'?s? \|specific \|different )(?:\w+ )?(?:units?\|ships?\|fleets?\|weapons?\|designs?\|compositions?\|strateg\w+\|armies\|army\|builds?\|doctrines?)` | `counter[- ]?(?:intuitive\|productive\|part\|strike\|espionage\|intelligence\|offer)s?\|over the counter\|counter-?point\|turn counter\|(?:a\|the) counter (?:on\|at\|for\|in) the` |
| combat_depth | `combat (?:is\|was\|feels\|felt\|seems\|system is) (?:\w+ ){0,2}(?:shallow\|boring\|fun\|great\|deep\|dull\|bland\|simple\|simplistic\|lacking\|engaging\|exciting\|satisfying\|meh\|bad\|terrible\|awful\|good\|amazing\|lackluster\|uninspired\|basic\|clunky\|weak\|solid)\|combat systems?\|combat mechanics?\|combat depth\|depth (?:of\|to\|in) (?:the )?combat` | — |
| numbers_win | `bigger (?:fleet\|army\|stack\|doomstack\|number)s? (?:always \|just \|simply )?wins?\|numbers game\|just numbers\|(?:whoever\|who) has (?:the )?(?:more\|bigger\|largest\|biggest) (?:\w+ )?(?:fleet\|army\|ships\|units\|stack)s?\|(?:biggest\|largest) (?:fleet\|army\|stack) wins\|(?:just\|simply\|only) (?:a )?(?:comparison\|comparing) (?:of )?(?:numbers\|fleet power\|strength)\|(?:compar\w*\|matter of\|all about\|comes down to) (?:the )?(?:fleet\|military\|army) (?:power\|strength)\|bigger number wins?` | — |
| retreat_pursuit | `retreat\w*\|disengag\w*\|emergency ftl\|(?:pursu\w*\|chas(?:e\|es\|ed\|ing)) (?:down )?(?:the \|an \|enemy \|their \|your \|retreating \|fleeing )?(?:\w+ )?(?:fleets?\|armies\|army\|ships?\|units?\|enemy\|enemies\|doomstacks?)\|(?:fleets?\|ships?\|armies\|army\|units?) (?:\w+ ){0,2}(?:run(?:s\|ning)? away\|chas\w*)` | `(?:the )?(?:victory\|goals?\|achievements?\|high scores?\|win conditions?)` |

LATE_GAME: `\b(?:late[- ]?game\|lategame\|end[- ]?game\|mid[- ]?game\|midgame)\b`
