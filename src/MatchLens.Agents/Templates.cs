using System.Globalization;
using MatchLens.Analytics;
using MatchLens.Contracts;

namespace MatchLens.Agents;

/// <summary>
/// Deterministic fallback for every model step, in English and Finnish. Used when a model call fails or times out,
/// when a model draft fails validation or grounding, and for the whole pipeline in offline mode.
/// Built only from measured facts, so it always passes validation.
/// </summary>
public static class Templates
{
    public static AnalysisResult Analysis(Candidate c) => new()
    {
        WhyItMatters = c.Kind switch
        {
            InsightKind.PressingShift => "The team is winning the ball high far more often than before, and it is producing shots.",
            InsightKind.PossessionIllusion => "Plenty of the ball, but it is not turning into chances.",
            InsightKind.TurnoverToChance => "A high regain turned straight into a shot before the opponent could reorganise.",
            InsightKind.PlayerMilestone => "The match's leading passer has reached a passing milestone.",
            InsightKind.Goal => "A goal changes the match; the move behind it shows how it was made.",
            InsightKind.TacticalShift => Tag(c, "shift") switch
            {
                "formation" => "The coach has changed the team's structure; the shape on the pitch shows it.",
                "deeper" => "The team has stopped pressing high and is defending from its own half.",
                _ => "The team is playing longer balls, skipping the midfield.",
            },
            InsightKind.PlayerRole => "Where this player gets the ball says more about the plan than the team sheet does.",
            InsightKind.Territory => "The team is getting to the space near the opponent's goal first, not just keeping the ball.",
            InsightKind.BetterPass => "The pass played was safe, but a teammate in a more dangerous spot was there to be found.",
            _ => "A notable moment in the match.",
        },
        KeyFactKeys = c.Facts.Keys.Take(4).ToList(),
        CitedEventIds = c.EvidenceEventIds.Take(5).ToList(),
        PlayerIds = c.PlayerId is null ? new List<string>() : new List<string> { c.PlayerId },
    };

    public static NarrativeDraft Draft(Candidate c, Persona persona, MatchInfo info)
    {
        bool fi = persona.Language == "fi";
        string team = info.Team(c.TeamId).Name;
        string player = info.Player(c.PlayerId)?.Name ?? team;
        double F(string key) => c.Facts.TryGetValue(key, out double v) ? v : 0;
        string N(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        string D(double v) => fi ? v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') : v.ToString("0.00", CultureInfo.InvariantCulture);
        string P(double share) => N(share * 100) + (fi ? " %" : "%");

        (string headline, string summary, List<string> figures) = c.Kind switch
        {
            InsightKind.PressingShift => fi
                ? ($"Prässi puree: {team}",
                   $"{team} on voittanut pallon korkealla {N(F("highRecoveries"))} kertaa viimeisen {N(F("windowMinutes"))} minuutin aikana (aiemmin {N(F("highRecoveriesBefore"))}), ja {N(F("shotsAfterRecovery"))} riistoa johti laukaukseen.",
                   new List<string> { "highRecoveries", "shotsAfterRecovery" })
                : ($"{team}'s press is biting",
                   $"{team} have won the ball high {N(F("highRecoveries"))} times in the last {N(F("windowMinutes"))} minutes, up from {N(F("highRecoveriesBefore"))}, and {N(F("shotsAfterRecovery"))} of those regains led to shots.",
                   new List<string> { "highRecoveries", "shotsAfterRecovery" }),

            InsightKind.PossessionIllusion => fi
                ? ("Pallonhallinta ei kerro kaikkea",
                   F("shots") == 0
                       ? $"{team} on pitänyt palloa {P(F("possessionShare"))} viimeisen {N(F("windowMinutes"))} minuutin ajan, mutta ei ole laukonut kertaakaan."
                       : $"{team} on pitänyt palloa {P(F("possessionShare"))} viimeisen {N(F("windowMinutes"))} minuutin ajan, mutta laukauksia on vain {N(F("shots"))} (xG {D(F("xg"))}).",
                   new List<string> { "possessionShare", "shots" })
                : ("Possession isn't telling the whole story",
                   F("shots") == 0
                       ? $"{team} have had {P(F("possessionShare"))} of the ball in the last {N(F("windowMinutes"))} minutes without a single shot."
                       : $"{team} have had {P(F("possessionShare"))} of the ball in the last {N(F("windowMinutes"))} minutes but only {N(F("shots"))} shot ({D(F("xg"))} xG).",
                   new List<string> { "possessionShare", "shots" }),

            InsightKind.TurnoverToChance when F("goal") == 1 => fi
                ? ($"{player} rankaisee riistosta",
                   $"{team} voitti pallon korkealla ja teki maalin {N(F("secondsToShot"))} sekunnin kuluttua.",
                   new List<string> { "secondsToShot", "xg" })
                : ($"{player} punishes the turnover",
                   $"{team} won the ball high and scored {N(F("secondsToShot"))} seconds later.",
                   new List<string> { "secondsToShot", "xg" }),

            InsightKind.TurnoverToChance => fi
                ? ("Korkea riisto, heti paikka",
                   $"{team} laukoi {N(F("secondsToShot"))} sekuntia korkean pallonriiston jälkeen (xG {D(F("xg"))}).",
                   new List<string> { "secondsToShot", "xg" })
                : ($"{team} turn a high regain into a chance",
                   $"{team} had a shot {N(F("secondsToShot"))} seconds after winning the ball high ({D(F("xg"))} xG).",
                   new List<string> { "secondsToShot", "xg" }),

            InsightKind.Goal when F("penalty") == 1 => fi
                ? ($"Maali pilkulta! {player}", $"{player} viimeisteli rangaistuspotkun maaliin joukkueelle {team}.", new List<string> { "xg" })
                : ($"{player} scores from the spot", $"{player} converts the penalty for {team}.", new List<string> { "xg" }),

            InsightKind.Goal => fi
                ? ($"Maali! {player}",
                   F("passesInMove") == 0
                       ? $"{player} viimeisteli maalin {team}:lle (xG {D(F("xg"))})."
                       : $"{player} viimeisteli hyökkäyksen, jossa {team} syötti {N(F("passesInMove"))} kertaa {N(F("moveSeconds"))} sekunnissa.",
                   new List<string> { "passesInMove", "xg" })
                : ($"{player} scores for {team}",
                   F("passesInMove") == 0
                       ? $"{player} finished for {team} ({D(F("xg"))} xG)."
                       : $"{player} finished a {N(F("passesInMove"))}-pass move that took {team} {N(F("moveSeconds"))} seconds.",
                   new List<string> { "passesInMove", "xg" }),

            InsightKind.PlayerMilestone => fi
                ? ($"{player}: {N(F("completedPasses"))} onnistunutta syöttöä",
                   $"{player} on onnistunut {N(F("completedPasses"))} syötössä {P(F("passAccuracy"))}:n tarkkuudella, eniten tässä ottelussa.",
                   new List<string> { "completedPasses", "passAccuracy" })
                : ($"{player} reaches {N(F("completedPasses"))} passes",
                   $"{player} has completed {N(F("completedPasses"))} passes at {P(F("passAccuracy"))} accuracy, the most in this match.",
                   new List<string> { "completedPasses", "passAccuracy" }),

            InsightKind.TacticalShift when Tag(c, "shift") == "formation" => fi
                ? ($"{team} vaihtaa muotoon {Tag(c, "formationAfter")}",
                   $"{team} pelaa nyt {Tag(c, "formationAfter")}-muodossa ({Tag(c, "formationBefore")} aiemmin): takalinjassa {N(F("defendersAfter"))} pelaajaa ja kärjessä {N(F("forwardsAfter"))}{CauseFi(c)}.",
                   new List<string> { "defendersAfter", "forwardsAfter" })
                : ($"{team} switch to a {Tag(c, "formationAfter")}",
                   $"{team} have changed from a {Tag(c, "formationBefore")} to a {Tag(c, "formationAfter")}: {N(F("defendersAfter"))} at the back and {N(F("forwardsAfter"))} up front{CauseEn(c)}.",
                   new List<string> { "defendersAfter", "forwardsAfter" }),

            InsightKind.TacticalShift when Tag(c, "shift") == "deeper" => fi
                ? ($"{team} vetäytyy syvemmälle",
                   $"{team} on lopettanut korkean prässin: viimeisen {N(F("windowMinutes"))} minuutin aikana se on prässännyt {P(F("highPressRateAfter"))} vastustajan omalla puoliskolla tekemistä syötöistä, aiemmin {P(F("highPressRateBefore"))}{CauseFi(c)}.",
                   new List<string> { "highPressRateBefore", "highPressRateAfter" })
                : ($"{team} sit deeper",
                   $"{team} have stopped pressing high: they pressed {P(F("highPressRateAfter"))} of the opponent's passes in their own half in the last {N(F("windowMinutes"))} minutes, down from {P(F("highPressRateBefore"))}{CauseEn(c)}.",
                   new List<string> { "highPressRateBefore", "highPressRateAfter" }),

            InsightKind.TacticalShift => fi
                ? ($"{team} siirtyy pitkiin palloihin",
                   $"{P(F("longBallShareAfter"))} joukkueen {team} syötöistä on nyt pitkiä palloja, kun aiemmin osuus oli {P(F("longBallShareBefore"))}{CauseFi(c)}.",
                   new List<string> { "longBallShareBefore", "longBallShareAfter" })
                : ($"{team} go direct",
                   $"{P(F("longBallShareAfter"))} of {team}'s passes are now long balls, up from {P(F("longBallShareBefore"))}{CauseEn(c)}.",
                   new List<string> { "longBallShareBefore", "longBallShareAfter" }),

            InsightKind.PlayerRole => RoleText(c, player, team, fi, N),

            InsightKind.Territory => fi
                ? ($"{team} ottaa hyökkäyskolmanneksen haltuunsa",
                   $"{team} ehtisi ensimmäisenä {P(F("finalThirdControl"))} hyökkäyskolmanneksesta viimeisen {N(F("windowMinutes"))} minuutin aikana (aiemmin {P(F("finalThirdControlBefore"))}), ja laukauksia tuli {N(F("shots"))}.",
                   new List<string> { "finalThirdControl", "finalThirdControlBefore" })
                : ($"{team} take over the final third",
                   $"{team} would get to {P(F("finalThirdControl"))} of the final third first over the last {N(F("windowMinutes"))} minutes, up from {P(F("finalThirdControlBefore"))}, and had {N(F("shots"))} shots in that time.",
                   new List<string> { "finalThirdControl", "finalThirdControlBefore" }),

            InsightKind.BetterPass => fi
                ? ($"{player} ohittaa paremman syötön",
                   $"{player} jätti paremmin sijoittuneen joukkuetoverin löytämättä {N(F("missedOptions"))} kertaa {N(F("passes"))} syötöstä viimeisen {N(F("windowMinutes"))} minuutin aikana, useimmiten {Tag(c, "receiverName")}; ne syötöt olisivat tuoneet {D(F("xtLeft"))} enemmän odotettua uhkaa (xT).",
                   new List<string> { "missedOptions", "xtLeft" })
                : ($"{player} keeps missing the better pass",
                   $"{player} had a better-placed teammate on {N(F("missedOptions"))} of {N(F("passes"))} passes in the last {N(F("windowMinutes"))} minutes, most often {Tag(c, "receiverName")}; those passes would have carried {D(F("xtLeft"))} more expected threat (xT).",
                   new List<string> { "missedOptions", "xtLeft" }),

            _ => (team, team, new List<string>()),
        };

        if (persona.Audience == Audience.Beginner)
            summary = summary + " " + WhyItMatters(c, fi);
        return new NarrativeDraft { Headline = headline, Summary = summary, FigureKeys = figures };
    }

    /// <summary>For beginners: one plain sentence on why this kind of moment matters, with no numbers.</summary>
    public static string WhyItMatters(Candidate c, bool fi) => (c.Kind, fi) switch
    {
        (InsightKind.PressingShift, false) => "Winning the ball close to the other team's goal leaves them no time to defend, which is why it so often leads to chances.",
        (InsightKind.PressingShift, true) => "Kun pallo voitetaan lähellä vastustajan maalia, puolustajilla ei ole aikaa järjestäytyä, ja siksi siitä syntyy usein maalipaikkoja.",
        (InsightKind.PossessionIllusion, false) => "Having the ball a lot only matters if you can get it near the goal; a team defending deep can make that very hard.",
        (InsightKind.PossessionIllusion, true) => "Pallonhallinnasta on hyötyä vain, jos pallo saadaan lähelle maalia; syvällä puolustava joukkue voi tehdä sen hyvin vaikeaksi.",
        (InsightKind.TurnoverToChance, false) => "Attacking straight after winning the ball catches the other team out of position.",
        (InsightKind.TurnoverToChance, true) => "Heti pallonriiston jälkeen hyökätessä vastustaja on vielä epäjärjestyksessä.",
        (InsightKind.Goal, false) => "Goals decide matches, so the team that scores often changes how both sides play next.",
        (InsightKind.Goal, true) => "Maalit ratkaisevat ottelut, joten maalin jälkeen molemmat joukkueet usein muuttavat pelitapaansa.",
        (InsightKind.PlayerMilestone, false) => "Lots of accurate passes mean this player is the one keeping the ball moving for the team.",
        (InsightKind.PlayerMilestone, true) => "Paljon onnistuneita syöttöjä tarkoittaa, että tämä pelaaja pitää joukkueen pelin liikkeessä.",
        (InsightKind.TacticalShift, false) => "Coaches change the team's shape to fix a problem or chase a result, so watch how the game changes next.",
        (InsightKind.TacticalShift, true) => "Valmentaja muuttaa joukkueen muotoa korjatakseen ongelman tai tavoitellakseen tulosta, joten seuraa miten peli nyt muuttuu.",
        (InsightKind.PlayerRole, false) => "Where a player actually plays tells you what the coach wants from them, sometimes more than their position on the team sheet.",
        (InsightKind.PlayerRole, true) => "Se, missä pelaaja oikeasti pelaa, kertoo mitä valmentaja häneltä haluaa, joskus enemmän kuin kokoonpanon pelipaikka.",
        (InsightKind.Territory, false) => "The team that gets to the space near the other goal first is the one making chances, even without having more of the ball.",
        (InsightKind.Territory, true) => "Joukkue, joka ehtii ensimmäisenä vastustajan maalin lähelle, luo maalipaikkoja, vaikka sillä ei olisi enempää palloa.",
        (InsightKind.BetterPass, false) => "Expected threat says how dangerous a spot is; finding the teammate in the more dangerous spot makes a goal more likely.",
        (InsightKind.BetterPass, true) => "Odotettu uhka (xT) kertoo, kuinka vaarallinen paikka on; syöttö vaarallisemmalla paikalla olevalle joukkuetoverille tekee maalista todennäköisemmän.",
        (_, false) => "This is one of the moments that shapes the match.",
        _ => "Tämä on yksi ottelun suuntaa muuttavista hetkistä.",
    };

    public static string Tag(Candidate c, string key) => c.Tags?.GetValueOrDefault(key) ?? "";

    private static string CauseEn(Candidate c) => Tag(c, "cause") switch
    {
        "substitution" => ", after a substitution",
        "goalFor" => ", after their goal",
        "goalAgainst" => ", after conceding",
        "halfTime" => ", after the break",
        _ => "",
    };

    private static string CauseFi(Candidate c) => Tag(c, "cause") switch
    {
        "substitution" => " vaihdon jälkeen",
        "goalFor" => " oman maalin jälkeen",
        "goalAgainst" => " päästetyn maalin jälkeen",
        "halfTime" => " tauon jälkeen",
        _ => "",
    };

    private static (string, string, List<string>) RoleText(Candidate c, string player, string team, bool fi, Func<double, string> n)
    {
        double F(string key) => c.Facts.TryGetValue(key, out double v) ? v : 0;
        string depth = n(F("averageDepthMetres")), touches = n(F("touches")), box = n(F("boxTouches"));
        var figures = new List<string> { "touches", "averageDepthMetres" };
        return (Tag(c, "role"), fi) switch
        {
            ("falseNine", true) => ($"{player}: valeysi", $"{player} on nimellisesti kärki, mutta on pelannut keskimäärin {depth} metrin päässä oman maalin edestä ja tullut keskikentälle kuin valeysi.", figures),
            ("falseNine", false) => ($"{player} is playing as a false nine", $"{player} is the nominal centre-forward but has averaged {depth} metres from {team}'s goal, dropping into midfield like a false nine.", figures),
            ("secondStriker", true) => ($"{player}: toinen kärki", $"{player} pelaa kärkihyökkääjän takana toisena kärkenä: {touches} kosketusta, joista {box} rangaistusalueella.", new List<string> { "touches", "boxTouches" }),
            ("secondStriker", false) => ($"{player} is the second striker", $"{player} is playing just behind the main striker as a second striker: {touches} touches, {box} of them in the box.", new List<string> { "touches", "boxTouches" }),
            ("shadowStriker", true) => ($"{player}: varjokärki", $"{player} saapuu keskikentältä rangaistusalueelle varjokärkenä: {box} kosketusta {touches}:stä boksissa.", new List<string> { "boxTouches", "touches" }),
            ("shadowStriker", false) => ($"{player}, the shadow striker", $"{player} keeps arriving in the box from midfield as a shadow striker: {box} of {touches} touches in the area.", new List<string> { "boxTouches", "touches" }),
            ("invertedWinger", true) => ($"{player} hakeutuu sisään", $"{player} lähtee laidalta mutta hakeutuu jatkuvasti keskelle: sisäänpäin hakeutuva laituri, {touches} kosketusta.", figures),
            ("invertedWinger", false) => ($"{player} cutting inside", $"{player} starts wide but keeps coming inside as an inverted winger, averaging {depth} metres from {team}'s goal over {touches} touches.", figures),
            ("wingBack", true) => ($"{player}: laitatukimies", $"{player} kattaa koko laidan laitatukimiehenä, keskimäärin {depth} metrin päässä oman maalin edestä.", figures),
            ("wingBack", false) => ($"{player} at wing-back", $"{player} is covering the whole flank as a wing-back, averaging {depth} metres from {team}'s goal.", figures),
            _ => (player, $"{player} ({team}).", figures),
        };
    }

    /// <summary>On-screen labels for every fact a detector measures, in English and Finnish.</summary>
    private static readonly Dictionary<string, (string En, string Fi)> Labels = new()
    {
        ["highRecoveries"] = ("High turnovers", "Korkeat riistot"),
        ["highRecoveriesBefore"] = ("High turnovers before", "Riistot ennen"),
        ["shotsAfterRecovery"] = ("Shots from regains", "Laukaukset riistoista"),
        ["xgAfterRecovery"] = ("xG from regains", "xG riistoista"),
        ["opponentPossessionShare"] = ("Opponent possession", "Vastustajan pallonhallinta"),
        ["possessionShare"] = ("Possession", "Pallonhallinta"),
        ["completedPasses"] = ("Completed passes", "Onnistuneet syötöt"),
        ["opponentCompletedPasses"] = ("Opponent completed passes", "Vastustajan onnistuneet syötöt"),
        ["finalThirdEntries"] = ("Final-third entries", "Hyökkäyskolmannekseen"),
        ["shots"] = ("Shots", "Laukaukset"),
        ["opponentShots"] = ("Opponent shots", "Vastustajan laukaukset"),
        ["xg"] = ("xG", "xG"),
        ["secondsToShot"] = ("Seconds to shot", "Sekuntia laukaukseen"),
        ["passesBeforeShot"] = ("Passes before shot", "Syötöt ennen laukausta"),
        ["passesInMove"] = ("Passes in the move", "Syötöt hyökkäyksessä"),
        ["moveSeconds"] = ("Length of the move", "Hyökkäyksen kesto"),
        ["passes"] = ("Passes attempted", "Syöttöyritykset"),
        ["passAccuracy"] = ("Pass accuracy", "Syöttötarkkuus"),
        ["nextBestCompletedPasses"] = ("Next best passer", "Seuraavaksi paras syöttäjä"),
        ["defendersBefore"] = ("Defenders before", "Puolustajat ennen"),
        ["defendersAfter"] = ("Defenders now", "Puolustajat nyt"),
        ["forwardsBefore"] = ("Forwards before", "Hyökkääjät ennen"),
        ["forwardsAfter"] = ("Forwards now", "Hyökkääjät nyt"),
        ["highPressRateBefore"] = ("High press before", "Korkea prässi ennen"),
        ["highPressRateAfter"] = ("High press now", "Korkea prässi nyt"),
        ["highPressuresAfter"] = ("High pressures", "Korkeat prässäykset"),
        ["pressuresAfter"] = ("Pressures now", "Prässäykset nyt"),
        ["pressuresBefore"] = ("Pressures before", "Prässäykset ennen"),
        ["longBallShareBefore"] = ("Long balls before", "Pitkät pallot ennen"),
        ["longBallShareAfter"] = ("Long balls now", "Pitkät pallot nyt"),
        ["longBallsAfter"] = ("Long balls", "Pitkät pallot"),
        ["passesAfter"] = ("Passes", "Syötöt"),
        ["touches"] = ("Touches", "Kosketukset"),
        ["averageDepthMetres"] = ("Average depth", "Keskisyvyys"),
        ["boxTouches"] = ("Touches in the box", "Kosketukset boksissa"),
        ["finalThirdControl"] = ("Final-third control", "Hyökkäyskolmanneksen hallinta"),
        ["finalThirdControlBefore"] = ("Control before", "Hallinta ennen"),
        ["finalThirdPasses"] = ("Passes into the final third", "Syötöt hyökkäyskolmannekseen"),
        ["missedOptions"] = ("Better options missed", "Ohitetut paremmat vaihtoehdot"),
        ["xtLeft"] = ("Threat left (xT)", "Käyttämätön uhka (xT)"),
        ["bestGain"] = ("Best option (xT)", "Paras vaihtoehto (xT)"),
    };

    public static string FigureLabel(string key, string language) =>
        Labels.TryGetValue(key, out var label) ? (language == "fi" ? label.Fi : label.En) : key;

    public static string? FigureUnit(string key) => key switch
    {
        "possessionShare" or "passAccuracy" or "opponentPossessionShare" or "highPressRateBefore" or "highPressRateAfter"
            or "longBallShareBefore" or "longBallShareAfter" or "finalThirdControl" or "finalThirdControlBefore" => "%",
        "averageDepthMetres" => "m",
        "secondsToShot" or "moveSeconds" => "s",
        _ => null,
    };
}
