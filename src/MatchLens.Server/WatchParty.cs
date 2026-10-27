using System.Text.RegularExpressions;
using MatchLens.Agents;
using MatchLens.Analytics;
using MatchLens.Contracts;
using MatchLens.Decisions;
using Microsoft.AspNetCore.SignalR;

namespace MatchLens.Server;

// ------------------------------------------------------------------------------------------------------------------
// Watch Party: friends watch the same match together: reactions, a short screened chat, polls on what Second Ball IQ finds,
// crowd predictions against Second Ball IQ's own, and room questions that Second Ball IQ investigates for everyone.
// ------------------------------------------------------------------------------------------------------------------

public sealed record PartyMember(string MemberId, string Name, string? TeamId, int Points, bool Simulated);

public sealed record PartyOption(string Id, string Label, string LabelFi, int Votes);

/// <summary>
/// A question to the room about something Second Ball IQ just found, and once it closes, Second Ball IQ's view. A tactical-test
/// poll (<paramref name="TestId"/> set) asks whether a locked criterion will hold: votes close after 40 seconds, the
/// match settles it at <paramref name="DeadlineSec"/>, and the room's votes are kept apart from the result.
/// </summary>
public sealed record PartyPoll(
    string PollId, string CandidateId, int ClockSec, string Question, string QuestionFi, IReadOnlyList<PartyOption> Options,
    long ClosesAtMs, bool Closed, string MatchLensPick, string? Verdict, string? VerdictFi,
    IReadOnlyDictionary<string, string>? Votes = null,
    string? TestId = null, int? DeadlineSec = null, string? Outcome = null, double? Estimate = null);

/// <summary>The room's picks for one prediction, against Second Ball IQ's pick and what happened.</summary>
public sealed record PartyPicks(
    string PredictionId, string Kind, string? PlayerName, IReadOnlyList<PartyOption> Options, string MatchLensPick,
    string? CrowdPick, string? Outcome, IReadOnlyList<string> RightNames, IReadOnlyDictionary<string, string>? ByMember = null);

public sealed record PartyQuestion(
    string QuestionId, string Text, string By, string Language, int Votes, string Status, AskResult? Answer, long AtMs);

public sealed record PartyState(
    string Code, string Host, IReadOnlyList<PartyMember> Members, IReadOnlyList<PartyPoll> Polls, IReadOnlyList<PartyPicks> Picks,
    IReadOnlyList<PartyQuestion> Questions, bool Demo, PartyTifo? Tifo = null, IReadOnlyList<PartyMessage>? Messages = null);

/// <summary>A fan's message in the room's feed. Screened before anyone sees it; simulated fans are marked.</summary>
public sealed record PartyMessage(string MessageId, string MemberId, string Name, string? TeamId, string Text, long AtMs, bool Simulated);

public sealed record PartyReaction(string Name, string Emoji, long AtMs, string? MemberId = null);

/// <summary>
/// A digital tifo: one side of the room raises cards together and, at the reveal, the stand shows a mosaic in the
/// club's colours. Designs are our own, drawn from the kit (stripes, a shirt, a star, the club's short name).
/// </summary>
public sealed record PartyTifo(
    string TifoId, string TeamId, string Design, string By, long StartedAtMs, long RevealAtMs, long EndsAtMs,
    IReadOnlyList<string> Holders, long NowMs);

public sealed record PartyShare(string Name, string Label, IReadOnlyList<string> EventIds, long AtMs);

/// <summary>
/// Owns the rooms. Listens to what the current match broadcasts: a pressing shift, a possession illusion or a tactical
/// shift opens a poll; a set-piece or next-goal prediction opens crowd picks, settled with points when Second Ball IQ
/// settles its own. Room questions are screened (length, a word list, and Decision-1 when live) before anyone sees
/// them; the most upvoted goes to Ask Second Ball IQ and the answer goes to the whole room. Demo rooms add a few clearly
/// labelled simulated fans so one presenter can show a lively room. Everything is in memory.
/// </summary>
public sealed class PartyHost : IDisposable
{
    public const int PollSeconds = 40;
    private static readonly string[] Emojis = ["🔥", "😱", "👏", "🤔"];
    private static readonly string[] Blocked = ["idiot", "stupid", "hate", "kill", "vittu", "perkele", "paska", "saatana", "fuck", "shit"];

    private readonly IHubContext<MatchHub> _hub;
    private readonly MatchHost _host;
    private readonly ILogger<PartyHost> _log;
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _roomOf = new();   // connection id -> room code
    private readonly object _sync = new();
    private readonly Timer _timer;
    private readonly Random _rng = new();

    public PartyHost(IHubContext<MatchHub> hub, MatchHost host, ILogger<PartyHost> log)
    {
        _hub = hub;
        _host = host;
        _log = log;
        _host.SessionStarted += OnSession;
        if (_host.Current is { } s) OnSession(s);
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    private sealed class Member
    {
        public required string Id, Name;
        public string? TeamId;
        public int Points;
        public bool Simulated;
        public string? Connection;
        // Rate limits, per member (milliseconds since the epoch of the last accepted action).
        public long LastReaction, LastQuestion, LastShare, LastMessage;
    }

    // Limits. The word list and the Decision-1 screen are the moderation; these keep any one member from flooding a room.
    private const int ReactionGapMs = 400, QuestionGapMs = 15_000, ShareGapMs = 5_000;
    private const int OpenQuestionsPerMember = 3, OpenQuestionsPerRoom = 12;
    private static readonly Regex EventIdPattern = new(@"^evt-\d{1,7}$", RegexOptions.Compiled);

    private sealed class Poll
    {
        public required string Id, CandidateId, Question, QuestionFi, MatchLensPick;
        public required int ClockSec;
        public required List<(string Id, string En, string Fi)> Options;
        public Dictionary<string, string> Votes = new();   // member -> option
        public long ClosesAt;
        public bool Closed;
        public string? Verdict, VerdictFi;
        public required Func<string, string, (string En, string Fi)> MakeVerdict;
        // Tactical tests only.
        public string? TestId, Outcome;
        public int? DeadlineSec;
        public double? Estimate;
    }

    private sealed class Picks
    {
        public required Prediction Prediction;
        public Dictionary<string, string> ByMember = new();
        public string? Outcome;
        public List<string> Right = [];
    }

    private sealed class Question
    {
        public required string Id, Text, By, ById, Language;
        public HashSet<string> Voters = [];
        public string Status = "open";   // open, investigating, answered
        public AskResult? Answer;
        public long At;
    }

    private sealed class Room
    {
        public required string Code, Host;
        public bool Demo;
        public Dictionary<string, Member> Members = new();
        public List<Poll> Polls = [];
        public Dictionary<string, Picks> Picks = new();
        public List<Question> Questions = [];
        public List<PartyMessage> Messages = [];
        public List<(long At, Action Act)> Scheduled = [];
        public long LastInvestigation;
        public int SimQuestions;
        public Tifo? Tifo;
        public long LastTifo;
    }

    private sealed class Tifo
    {
        public required string Id, TeamId, Design, By;
        public long StartedAt, RevealAt, EndsAt;
        public HashSet<string> Holders = [];
    }

    public static readonly string[] TifoDesigns = ["stripes", "shirt", "star", "letters"];
    private const int TifoGatherMs = 12_000, TifoShowMs = 10_000, TifoGapMs = 40_000;

    // --------------------------------------------------------------------------------------------------------------
    // Joining
    // --------------------------------------------------------------------------------------------------------------

    public PartyState Create(string connectionId, string name, string? teamId, bool demo)
    {
        string code;
        lock (_sync)
        {
            do code = new string(Enumerable.Range(0, 4).Select(_ => "ABCDEFGHJKLMNPQRSTUVWXYZ"[_rng.Next(24)]).ToArray());
            while (_rooms.ContainsKey(code));
            var room = new Room { Code = code, Host = Clean(name), Demo = demo };
            _rooms[code] = room;
            if (demo) AddSimulatedFans(room);
        }
        return Join(connectionId, code, name, teamId)!;
    }

    public PartyState? Join(string connectionId, string code, string name, string? teamId)
    {
        PartyState state;
        lock (_sync)
        {
            if (!_rooms.TryGetValue(code.Trim(), out Room? room)) return null;
            Leave(connectionId, notify: false);
            string clean = Clean(name);
            Member? existing = room.Members.Values.FirstOrDefault(m => !m.Simulated && m.Name == clean && m.Connection is null);
            Member member = existing ?? new Member { Id = Guid.NewGuid().ToString("N")[..8], Name = clean };
            member.TeamId = teamId;
            member.Connection = connectionId;
            room.Members[member.Id] = member;
            _roomOf[connectionId] = room.Code;
            state = View(room);
        }
        _ = _hub.Groups.AddToGroupAsync(connectionId, Group(code.Trim().ToUpperInvariant()));
        _ = Push(state.Code);
        return state;
    }

    public void Leave(string connectionId, bool notify = true)
    {
        string? code;
        lock (_sync)
        {
            if (!_roomOf.Remove(connectionId, out code)) return;
            if (_rooms.TryGetValue(code, out Room? room))
                foreach (Member m in room.Members.Values.Where(m => m.Connection == connectionId)) m.Connection = null;
        }
        _ = _hub.Groups.RemoveFromGroupAsync(connectionId, Group(code));
        if (notify) _ = Push(code);
    }

    public string? MemberOf(string connectionId)
    {
        lock (_sync)
        {
            if (!_roomOf.TryGetValue(connectionId, out string? code) || !_rooms.TryGetValue(code, out Room? room)) return null;
            return room.Members.Values.FirstOrDefault(m => m.Connection == connectionId)?.Id;
        }
    }

    // --------------------------------------------------------------------------------------------------------------
    // What members do
    // --------------------------------------------------------------------------------------------------------------

    public Task React(string connectionId, string emoji)
    {
        if (!Emojis.Contains(emoji)) return Task.CompletedTask;
        (Room room, Member me)? found = Find(connectionId);
        if (found is not { } f) return Task.CompletedTask;
        lock (_sync)
        {
            if (Now() - f.me.LastReaction < ReactionGapMs) return Task.CompletedTask;
            f.me.LastReaction = Now();
        }
        return _hub.Clients.Group(Group(f.room.Code)).SendAsync("partyReaction", new PartyReaction(f.me.Name, emoji, Now(), f.me.Id));
    }

    public Task Vote(string connectionId, string pollId, string optionId)
    {
        string? code = null;
        lock (_sync)
        {
            if (Find(connectionId) is not { } f) return Task.CompletedTask;
            Poll? poll = f.room.Polls.FirstOrDefault(p => p.Id == pollId);
            if (poll is null || poll.Closed || poll.Options.All(o => o.Id != optionId)) return Task.CompletedTask;
            poll.Votes[f.me.Id] = optionId;
            code = f.room.Code;
        }
        return Push(code);
    }

    public Task Pick(string connectionId, string predictionId, string optionId)
    {
        string? code = null;
        lock (_sync)
        {
            if (Find(connectionId) is not { } f) return Task.CompletedTask;
            if (!f.room.Picks.TryGetValue(predictionId, out Picks? picks) || picks.Outcome is not null) return Task.CompletedTask;
            if (picks.Prediction.Options.All(o => o.Id != optionId)) return Task.CompletedTask;
            picks.ByMember[f.me.Id] = optionId;
            code = f.room.Code;
        }
        return Push(code);
    }

    public Task Share(string connectionId, string label, string[] eventIds)
    {
        if (Find(connectionId) is not { } f || eventIds is null) return Task.CompletedTask;
        List<string> ids = eventIds.Where(id => id is not null && EventIdPattern.IsMatch(id)).Distinct().Take(20).ToList();
        if (ids.Count == 0) return Task.CompletedTask;
        lock (_sync)
        {
            if (Now() - f.me.LastShare < ShareGapMs) return Task.CompletedTask;
            f.me.LastShare = Now();
        }
        return _hub.Clients.Group(Group(f.room.Code)).SendAsync("partyShare",
            new PartyShare(f.me.Name, Clean(label ?? "", 80), ids, Now()));
    }

    /// <summary>A room question. Returns a reason when it is not accepted (shown only to the person who asked).</summary>
    // --------------------------------------------------------------------------------------------------------------
    // Fan chat: short messages in the room's feed, screened in layers like room questions
    // --------------------------------------------------------------------------------------------------------------

    private const int MessageGapMs = 3_000, MessagesKept = 60, MessageMaxChars = 160;

    /// <summary>
    /// Posts a fan's message to the room. Screened before anyone sees it: 1 to 160 characters, the word list, one
    /// message every 3 seconds per member, then a Decision-1 check that it is respectful (skipped, not trusted blindly,
    /// when Decision-1 is unavailable). Returns a reason when it is refused. Rendered as plain text by the browser.
    /// </summary>
    public async Task<string?> Say(string connectionId, string text, string language)
    {
        text = (text ?? "").Trim();
        bool fi = language == "fi";
        if (text.Length is < 1 or > MessageMaxChars) return fi ? "Viestin pituus 1–160 merkkiä." : "Messages need 1 to 160 characters.";
        if (Blocked.Any(w => Regex.IsMatch(text, $@"\b{w}", RegexOptions.IgnoreCase))) return fi ? "Pidetään keskustelu asiallisena." : "Let's keep it friendly.";
        if (Find(connectionId) is not { } f) return fi ? "Liity ensin juhliin." : "Join a party first.";
        lock (_sync)
        {
            if (Now() - f.me.LastMessage < MessageGapMs) return fi ? "Odota hetki ennen seuraavaa viestiä." : "Wait a moment before your next message.";
            f.me.LastMessage = Now();
        }
        if (await Disrespectful(text)) return fi ? "Pidetään keskustelu asiallisena." : "Let's keep it friendly.";
        lock (_sync) Post(f.room, f.me, text);
        await Push(f.room.Code);
        return null;
    }

    private void Post(Room room, Member by, string text)
    {
        room.Messages.Add(new PartyMessage(Guid.NewGuid().ToString("N")[..10], by.Id, by.Name, by.TeamId, text, Now(), by.Simulated));
        if (room.Messages.Count > MessagesKept) room.Messages.RemoveAt(0);
    }

    /// <summary>Decision-1 checks that a chat message is respectful; any failure or timeout lets it through on the word list.</summary>
    private async Task<bool> Disrespectful(string text)
    {
        if (_host.Decisions is not { } decisions || _host.Current is not { } session) return false;
        MatchInfo info = session.Info.Match;
        var questions = new Dictionary<string, DecisionQuestion>
        {
            ["respectful"] = new YesNoQuestion("Is this chat message respectful: free of abuse, hate, harassment, threats and personal attacks? Banter about the teams is fine."),
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            DecisionResult result = await decisions.DecideAsync(
                $"A fan watching {info.Home.Name} against {info.Away.Name} wrote in the watch party chat: \"{text}\"", questions, timeout.Token);
            return result.Get<YesNoAnswer>("respectful").Probability < 0.3;
        }
        catch (Exception ex)
        {
            _log.LogInformation("Chat screen unavailable ({Message}); accepted on the word list", ex.Message);
            return false;
        }
    }

    // Demo rooms: simulated fans say something after the big moments (always labelled as simulated).
    private static readonly string[] GoalLines = ["What a finish!", "Get in!", "Told you this was coming", "Unbelievable!", "That's the one!"];
    private static readonly string[] ConcededLines = ["Nooo", "Defending like that…", "We need a response now", "Wake up!"];
    private static readonly string[] MetLines = ["Called it, the read held up", "+2, easy", "The numbers don't lie", "Knew it would hold"];
    private static readonly string[] MissLines = ["Didn't see that coming", "So much for that read", "Match had other ideas"];
    private static readonly string[] ChanceLines = ["So close!", "How did that stay out?", "Keeper's having a day"];

    private void SimSay(Room room, Member fan, string[] lines)
    {
        string line = lines[_rng.Next(lines.Length)];
        Room r = room;
        Later(room, 1200, 4500, () => { lock (_sync) Post(r, fan, line); });
    }

    public async Task<string?> AskRoom(string connectionId, string text, string language)
    {
        text = (text ?? "").Trim();
        bool fi = language == "fi";
        if (text.Length is < 5 or > 200) return fi ? "Kysymyksen pituus 5–200 merkkiä." : "Questions need 5 to 200 characters.";
        if (Blocked.Any(w => Regex.IsMatch(text, $@"\b{w}", RegexOptions.IgnoreCase))) return fi ? "Pidetään keskustelu asiallisena." : "Let's keep it friendly.";
        if (Find(connectionId) is not { } f) return fi ? "Liity ensin juhliin." : "Join a party first.";
        lock (_sync)
        {
            if (Now() - f.me.LastQuestion < QuestionGapMs)
                return fi ? "Odota hetki ennen seuraavaa kysymystä." : "Wait a few seconds before your next question.";
            if (f.room.Questions.Count(q => q.Status == "open" && q.ById == f.me.Id) >= OpenQuestionsPerMember)
                return fi ? "Sinulla on jo kolme avointa kysymystä." : "You already have three open questions.";
            if (f.room.Questions.Count(q => q.Status == "open") >= OpenQuestionsPerRoom)
                return fi ? "Huoneessa on jo paljon avoimia kysymyksiä. Äänestä niitä." : "The room has plenty of open questions; upvote one instead.";
            f.me.LastQuestion = Now();
        }
        if (await OffTopic(text, f.room)) return fi ? "Second Ball IQ vastaa vain tätä ottelua koskeviin kysymyksiin." : "Second Ball IQ only takes questions about this match.";
        Add(f.room, f.me, text, language);
        await Push(f.room.Code);
        return null;
    }

    /// <summary>Start a tifo for the member's own club. Returns a reason when it cannot start now.</summary>
    public Task<string?> StartTifo(string connectionId, string design, string language)
    {
        bool fi = language == "fi";
        if (!TifoDesigns.Contains(design)) return Task.FromResult<string?>("Unknown design.");
        if (Find(connectionId) is not { } f) return Task.FromResult<string?>(fi ? "Liity ensin juhliin." : "Join a party first.");
        if (f.me.TeamId is not { } team)
            return Task.FromResult<string?>(fi ? "Valitse ensin joukkue, jota kannatat." : "Pick a club to support first: a tifo belongs to one end.");
        string? reason;
        lock (_sync) reason = StartTifoLocked(f.room, f.me, team, design, fi);
        if (reason is null) _ = Push(f.room.Code);
        return Task.FromResult(reason);
    }

    /// <summary>Hold up your card for the tifo your end is making.</summary>
    public Task HoldTifo(string connectionId)
    {
        string? code = null;
        lock (_sync)
        {
            if (Find(connectionId) is not { } f || f.room.Tifo is not { } t) return Task.CompletedTask;
            if (f.me.TeamId != t.TeamId || Now() >= t.EndsAt) return Task.CompletedTask;
            if (t.Holders.Add(f.me.Id)) code = f.room.Code;
        }
        return Push(code);
    }

    private string? StartTifoLocked(Room room, Member by, string teamId, string design, bool fi = false)
    {
        long now = Now();
        if (room.Tifo is { } live && now < live.EndsAt) return fi ? "Tifo on jo käynnissä." : "A tifo is already going up.";
        if (now - room.LastTifo < TifoGapMs) return fi ? "Hetki ennen seuraavaa tifoa." : "Give the stand a moment before the next tifo.";
        var tifo = new Tifo
        {
            Id = Guid.NewGuid().ToString("N")[..8], TeamId = teamId, Design = design, By = by.Name,
            StartedAt = now, RevealAt = now + TifoGatherMs, EndsAt = now + TifoGatherMs + TifoShowMs,
        };
        tifo.Holders.Add(by.Id);
        room.Tifo = tifo;
        room.LastTifo = now;
        // Simulated fans on that side join in, one by one.
        if (room.Demo)
            foreach (Member fan in room.Members.Values.Where(m => m.Simulated && m.TeamId == teamId && m.Id != by.Id))
            {
                string id = fan.Id;
                Later(room, 1200, TifoGatherMs - 1500, () => { lock (_sync) if (room.Tifo == tifo) tifo.Holders.Add(id); });
            }
        // One push at the reveal and one when it comes down, so every screen switches together.
        Later(room, TifoGatherMs, TifoGatherMs + 1, () => { });
        Later(room, TifoGatherMs + TifoShowMs, TifoGatherMs + TifoShowMs + 1, () => { });
        return null;
    }

    public Task Upvote(string connectionId, string questionId)
    {
        string? code = null;
        lock (_sync)
        {
            if (Find(connectionId) is not { } f) return Task.CompletedTask;
            Question? q = f.room.Questions.FirstOrDefault(x => x.Id == questionId);
            if (q is null || q.Status != "open") return Task.CompletedTask;
            q.Voters.Add(f.me.Id);
            code = f.room.Code;
        }
        return Push(code);
    }

    /// <summary>The host can send a question straight to Second Ball IQ without waiting for votes.</summary>
    public Task Investigate(string connectionId, string questionId)
    {
        Room? room;
        lock (_sync)
        {
            if (Find(connectionId) is not { } f || f.me.Name != f.room.Host) return Task.CompletedTask;
            room = f.room;
        }
        return InvestigateAsync(room, questionId);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Listening to the match
    // --------------------------------------------------------------------------------------------------------------

    private void OnSession(MatchSession session)
    {
        lock (_sync)
            foreach (Room room in _rooms.Values)
            {
                room.Polls.Clear();
                room.Picks.Clear();
                room.Questions.Clear();
                room.Messages.Clear();
                room.Scheduled.Clear();
                room.SimQuestions = 0;
                foreach (Member m in room.Members.Values) m.Points = 0;
                room.Tifo = null;
            }
        session.Sent += (message, payload) => OnMatch(session, message, payload);
        foreach (string code in Codes()) _ = Push(code);
    }

    private void OnMatch(MatchSession session, string message, object payload)
    {
        switch (payload)
        {
            case InsightView v when message == "insight" && v.Status == InsightStatus.Live && v.PersonaKey == "Casual-en":
                // A read that can be tested gets a test poll when its test opens; the opinion poll is the fallback when
                // no test opens (too late in the match, or the same read is already being tested).
                if (TacticalTests.Supports(v.Insight.Kind))
                    _ = Task.Delay(1500).ContinueWith(_ =>
                    {
                        if (!session.Tests().Any(x => x.CandidateId == v.CandidateId)) OpenPoll(session, v);
                    }, TaskScheduler.Default);
                else OpenPoll(session, v);
                break;
            case TacticalTest t when message == "test":
                OnTest(t);
                break;
            case Prediction p when message == "prediction":
                OnPrediction(p);
                break;
            case Call c when message == "call":
                SimReact(c);
                break;
        }
    }

    private void OpenPoll(MatchSession session, InsightView v)
    {
        CandidateView? c = session.Candidates().FirstOrDefault(x => x.CandidateId == v.CandidateId);
        if (c is null) return;
        MatchInfo info = session.Info.Match;
        string team = info.Team(c.TeamId).Name, opp = info.Team(info.OpponentOf(c.TeamId)).Name;
        double F(string k) => c.Facts.TryGetValue(k, out double x) ? x : 0;
        string T(string k) => c.Tags?.GetValueOrDefault(k) ?? "";
        string pct(double share) => $"{Math.Round(share * 100):0}%";

        (string q, string qFi, List<(string, string, string)> opts, string pick, Func<string, string, (string, string)> verdict)? spec = c.Kind switch
        {
            InsightKind.PressingShift => ($"{team}'s press is winning the ball high. Have they taken control?",
                $"{team}n prässi voittaa palloja korkealla. Onko se ottanut pelin hallintaansa?",
                [("yes", "Yes, the press is working", "Kyllä, prässi toimii"), ("early", "Too early to tell", "Liian aikaista sanoa"), ("counter", $"{opp} will exploit the space", $"{opp} käyttää tilan hyväkseen")],
                "yes", (crowd, share) => (
                    $"Second Ball IQ says yes: {F("highRecoveries"):0} high regains in {F("windowMinutes"):0} minutes, up from {F("highRecoveriesBefore"):0}, and {F("shotsAfterRecovery"):0} led to shots. {share} of you agreed.",
                    $"Second Ball IQ:n mukaan kyllä: {F("highRecoveries"):0} korkeaa riistoa {F("windowMinutes"):0} minuutissa (aiemmin {F("highRecoveriesBefore"):0}), joista {F("shotsAfterRecovery"):0} johti laukaukseen. {share} teistä oli samaa mieltä.")),
            InsightKind.PossessionIllusion => ($"{team} have most of the ball. Is it going anywhere?",
                $"{team} pitää palloa. Johtaako se mihinkään?",
                [("no", "No, it's sterile", "Ei, se on tehotonta"), ("patience", "Patience, a chance will come", "Kärsivällisyyttä, paikka tulee"), ("yes", "Yes, they're in control", "Kyllä, he hallitsevat")],
                "no", (crowd, share) => (
                    $"Second Ball IQ says it's sterile: {pct(F("possessionShare"))} of the ball but {F("shots"):0} shots worth {F("xg"):0.00} xG. {share} of you agreed.",
                    $"Second Ball IQ:n mukaan tehotonta: {pct(F("possessionShare"))} pallonhallinnasta mutta vain {F("shots"):0} laukausta, xG {F("xg"):0.00}. {share} teistä oli samaa mieltä.")),
            InsightKind.TacticalShift => (
                T("shift") switch
                {
                    "formation" => $"{team} switch from a {T("formationBefore")} to a {T("formationAfter")}. Will it pay off?",
                    "deeper" => $"{team} are sitting deep now. Right call?",
                    _ => $"{team} are going long. Will it work?",
                },
                T("shift") switch
                {
                    "formation" => $"{team} vaihtaa muodosta {T("formationBefore")} muotoon {T("formationAfter")}. Kannattaako se?",
                    "deeper" => $"{team} puolustaa nyt syvällä. Oikea ratkaisu?",
                    _ => $"{team} pelaa pitkää. Toimiiko se?",
                },
                [("yes", "Yes, smart change", "Kyllä, fiksu muutos"), ("early", "Too early to tell", "Liian aikaista sanoa"), ("no", "No, it will backfire", "Ei, se kostautuu")],
                "early", (crowd, share) => T("shift") == "formation"
                    ? ($"Second Ball IQ says it's too early to judge, but the change is real: {F("defendersBefore"):0} at the back became {F("defendersAfter"):0}, {F("forwardsBefore"):0} up front became {F("forwardsAfter"):0}. {share} of you said the same.",
                       $"Second Ball IQ:n mukaan on liian aikaista sanoa, mutta muutos on todellinen: takalinja {F("defendersBefore"):0} → {F("defendersAfter"):0}, kärjessä {F("forwardsBefore"):0} → {F("forwardsAfter"):0}. {share} teistä sanoi samoin.")
                    : ($"Second Ball IQ says it's too early to judge; ask it again in ten minutes. {share} of you said the same.",
                       $"Second Ball IQ:n mukaan on liian aikaista sanoa; kysy uudelleen kymmenen minuutin päästä. {share} teistä sanoi samoin.")),
            InsightKind.Territory => ($"{team} have taken over the final third. Will it bring a goal?",
                $"{team} on ottanut hyökkäyskolmanneksen haltuunsa. Syntyykö siitä maali?",
                [("yes", "Yes, a goal is coming", "Kyllä, maali on tulossa"), ("pressure", "Pressure, but no goal", "Painetta, mutta ei maalia"), ("fades", "It will fade", "Se hiipuu")],
                "pressure", (crowd, share) => (
                    $"Second Ball IQ reads it as pressure: {pct(F("finalThirdControl"))} of the final third is theirs now, up from {pct(F("finalThirdControlBefore"))}, with {F("shots"):0} shots so far. {share} of you agreed.",
                    $"Second Ball IQ:n mukaan painetta: {pct(F("finalThirdControl"))} hyökkäyskolmanneksesta on nyt heidän, aiemmin {pct(F("finalThirdControlBefore"))}, ja laukauksia {F("shots"):0}. {share} teistä oli samaa mieltä.")),
            _ => null,
        };
        if (spec is not { } s) return;

        lock (_sync)
            foreach (Room room in _rooms.Values)
            {
                if (room.Polls.Any(p => p.CandidateId == c.CandidateId)) continue;
                var poll = new Poll
                {
                    Id = $"poll-{c.CandidateId}", CandidateId = c.CandidateId, ClockSec = c.DetectedAtSec, Question = s.q, QuestionFi = s.qFi,
                    Options = s.opts, MatchLensPick = s.pick, ClosesAt = Now() + PollSeconds * 1000, MakeVerdict = s.verdict,
                };
                room.Polls.Add(poll);
                SimVote(room, poll);
            }
        foreach (string code in Codes()) _ = Push(code);
    }

    /// <summary>
    /// Tactical tests in the room: a poll opens when a test locks ("will it hold?"), votes lock after 40 seconds, and
    /// when the match settles the test the room sees the result, how it voted and Decision-1's estimate. A right call
    /// earns 2 points; an inconclusive test earns nothing. In a demo room a met test raises that side's tifo.
    /// </summary>
    private void OnTest(TacticalTest t)
    {
        MatchInfo? info = _host.Current?.Info.Match;
        if (info is null) return;
        string deadline = NarrativeText.Clock(t.DeadlineSec);
        lock (_sync)
            foreach (Room room in _rooms.Values)
            {
                Poll? poll = room.Polls.FirstOrDefault(p => p.TestId == t.TestId);
                if (poll is null)
                {
                    if (t.Status != TestStatus.Open || room.Polls.Any(p => p.CandidateId == t.CandidateId)) continue;
                    poll = new Poll
                    {
                        Id = $"poll-{t.TestId}", CandidateId = t.CandidateId, ClockSec = t.OpensAtSec, TestId = t.TestId, DeadlineSec = t.DeadlineSec,
                        Question = t.Criterion, QuestionFi = t.CriterionFi, Estimate = t.Estimate,
                        Options = [("yes", "It will hold", "Pitää"), ("no", "It won't", "Ei pidä")],
                        MatchLensPick = "yes", ClosesAt = Now() + PollSeconds * 1000,
                        MakeVerdict = (_, share) => (
                            $"Votes locked: {share} of you say it holds. The match decides by {deadline}.",
                            $"Äänet lukittu: {share} teistä sanoo, että pitää. Ottelu ratkaisee {deadline} mennessä."),
                    };
                    room.Polls.Add(poll);
                    SimVote(room, poll);
                }
                poll.Estimate = t.Estimate;
                if (t.Status == TestStatus.Open || poll.Outcome is not null) continue;

                poll.Outcome = t.Status switch { TestStatus.Met => "met", TestStatus.NotMet => "notMet", _ => "inconclusive" };
                poll.Closed = true;
                int total = poll.Votes.Count, yes = poll.Votes.Values.Count(v => v == "yes");
                string room_ = total == 0 ? "Nobody voted." : $"{Math.Round(100.0 * yes / total):0}% of the room said it would hold.";
                string roomFi = total == 0 ? "Kukaan ei äänestänyt." : $"{Math.Round(100.0 * yes / total):0} % huoneesta sanoi, että pitää.";
                string est = t.Estimate is { } e ? $" Decision-1's estimate was {Math.Round(e * 100):0}% (a model estimate, not a calibrated probability)." : "";
                string estFi = t.Estimate is { } e2 ? $" Decision-1:n arvio oli {Math.Round(e2 * 100):0} % (mallin arvio, ei kalibroitu todennäköisyys)." : "";
                poll.Verdict = $"{t.Result} {room_}{est}";
                poll.VerdictFi = $"{t.ResultFi} {roomFi}{estFi}";
                if (t.Status is TestStatus.Met or TestStatus.NotMet)
                {
                    string right = t.Status == TestStatus.Met ? "yes" : "no";
                    foreach ((string memberId, string vote) in poll.Votes)
                        if (vote == right && room.Members.TryGetValue(memberId, out Member? m)) m.Points += 2;
                }
                if (room.Demo && t.Status is TestStatus.Met or TestStatus.NotMet)
                {
                    string right = t.Status == TestStatus.Met ? "yes" : "no";
                    Member? talker = room.Members.Values.Where(f => f.Simulated && poll.Votes.GetValueOrDefault(f.Id) == right).OrderBy(_ => _rng.Next()).FirstOrDefault();
                    if (talker is not null) SimSay(room, talker, t.Status == TestStatus.Met ? MetLines : MissLines);
                }
                if (t.Status == TestStatus.Met && room.Demo)
                {
                    Member? starter = room.Members.Values.Where(f => f.Simulated && f.TeamId == t.TeamId).OrderBy(_ => _rng.Next()).FirstOrDefault();
                    Room r = room;
                    if (starter is not null)
                        Later(room, 1500, 3000, () => { lock (_sync) StartTifoLocked(r, starter, t.TeamId, "star"); });
                }
            }
        foreach (string code in Codes()) _ = Push(code);
    }

    private void OnPrediction(Prediction p)
    {
        lock (_sync)
            foreach (Room room in _rooms.Values)
            {
                if (!room.Picks.TryGetValue(p.PredictionId, out Picks? picks))
                {
                    if (p.Outcome is not null) continue;
                    room.Picks[p.PredictionId] = picks = new Picks { Prediction = p };
                    SimPick(room, picks);
                    continue;
                }
                picks.Prediction = p;
                if (p.Outcome is null || picks.Outcome is not null) continue;
                picks.Outcome = p.Outcome;
                bool machineRight = p.Correct == true;
                foreach ((string memberId, string pick) in picks.ByMember)
                {
                    if (pick != p.Outcome || !room.Members.TryGetValue(memberId, out Member? m)) continue;
                    m.Points += machineRight ? 3 : 5;   // beating Second Ball IQ is worth more
                    picks.Right.Add(m.Name);
                }
            }
        foreach (string code in Codes()) _ = Push(code);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Questions to Second Ball IQ
    // --------------------------------------------------------------------------------------------------------------

    private void Add(Room room, Member by, string text, string language)
    {
        lock (_sync)
        {
            var q = new Question { Id = Guid.NewGuid().ToString("N")[..8], Text = text, By = by.Name, ById = by.Id, Language = language, At = Now() };
            q.Voters.Add(by.Id);
            room.Questions.Add(q);
            if (room.Questions.Count > 20) room.Questions.RemoveAt(0);
            SimUpvote(room, q);
        }
    }

    /// <summary>Decision-1 screens room questions when live; offline, the word list is the only screen.</summary>
    private async Task<bool> OffTopic(string text, Room room)
    {
        if (_host.Decisions is not { } decisions || _host.Current is not { } session) return false;
        MatchInfo info = session.Info.Match;
        var questions = new Dictionary<string, DecisionQuestion>
        {
            ["onTopic"] = new YesNoQuestion("Is this a respectful question or comment about the football match being watched (its teams, players, tactics, events or rules)?"),
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            DecisionResult result = await decisions.DecideAsync(
                $"A viewer of the match {info.Home.Name} against {info.Away.Name} posted to the watch party: \"{text}\"", questions, timeout.Token);
            return result.Get<YesNoAnswer>("onTopic").Probability < 0.3;
        }
        catch (Exception ex)
        {
            _log.LogInformation("Room question screen unavailable ({Message}); accepted on the word list", ex.Message);
            return false;
        }
    }

    private async Task InvestigateAsync(Room room, string questionId)
    {
        Question? q;
        lock (_sync)
        {
            q = room.Questions.FirstOrDefault(x => x.Id == questionId);
            if (q is null || q.Status != "open" || room.Questions.Any(x => x.Status == "investigating")) return;
            q.Status = "investigating";
            room.LastInvestigation = Now();
        }
        await Push(room.Code);
        if (_host.Current is not { } session) return;
        try
        {
            AskResult answer = await session.AskAsync(new AskRequest(q.Text, Audience.Casual, q.Language), CancellationToken.None);
            lock (_sync) { q.Answer = answer; q.Status = "answered"; }
            _log.LogInformation("Party {Code} investigated \"{Question}\": {Status}", room.Code, q.Text, answer.Status);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Party question failed");
            lock (_sync) q.Status = "open";
        }
        await Push(room.Code);
    }

    // --------------------------------------------------------------------------------------------------------------
    // The clock: polls closing, questions reaching their votes, simulated fans
    // --------------------------------------------------------------------------------------------------------------

    private void Tick()
    {
        var push = new HashSet<string>();
        var investigate = new List<(Room, string)>();
        List<Action> due = [];
        lock (_sync)
        {
            long now = Now();
            foreach (Room room in _rooms.Values)
            {
                foreach (Poll poll in room.Polls.Where(p => !p.Closed && p.Verdict is null && p.ClosesAt <= now))
                {
                    poll.Closed = true;
                    int total = poll.Votes.Count, agree = poll.Votes.Values.Count(v => v == poll.MatchLensPick);
                    string share = total == 0 ? "None" : $"{Math.Round(100.0 * agree / total):0}%";
                    string shareFi = total == 0 ? "Kukaan" : $"{Math.Round(100.0 * agree / total):0} %";
                    (string en, _) = poll.MakeVerdict(poll.MatchLensPick, share);
                    (_, string fi) = poll.MakeVerdict(poll.MatchLensPick, shareFi);
                    poll.Verdict = en;
                    poll.VerdictFi = fi;
                    // A test poll scores when the match settles it, not when its votes lock.
                    if (poll.TestId is null)
                        foreach ((string memberId, string vote) in poll.Votes)
                            if (vote == poll.MatchLensPick && room.Members.TryGetValue(memberId, out Member? m)) m.Points += 1;
                    push.Add(room.Code);
                }
                int people = room.Members.Count;
                int needed = Math.Max(2, (people + 2) / 3);
                if (room.Questions.All(x => x.Status != "investigating") && now - room.LastInvestigation > 20_000)
                {
                    Question? top = room.Questions.Where(x => x.Status == "open" && x.Voters.Count >= needed)
                        .OrderByDescending(x => x.Voters.Count).FirstOrDefault();
                    if (top is not null) investigate.Add((room, top.Id));
                }
                foreach (var s in room.Scheduled.Where(s => s.At <= now).ToList())
                {
                    room.Scheduled.Remove(s);
                    due.Add(s.Act);
                    push.Add(room.Code);
                }
            }
        }
        foreach (Action act in due)
        {
            try { act(); } catch (Exception ex) { _log.LogDebug(ex, "Simulated fan action failed"); }
        }
        foreach (string code in push) _ = Push(code);
        foreach ((Room room, string id) in investigate) _ = InvestigateAsync(room, id);
    }

    // --------------------------------------------------------------------------------------------------------------
    // Simulated fans (demo rooms only, always labelled as simulated)
    // --------------------------------------------------------------------------------------------------------------

    private static readonly (string Name, bool Home)[] Fans =
        [("Aada", true), ("Mikko", true), ("Sofia", false), ("Leo", true), ("Noor", false), ("Jussi", false), ("Emma", true), ("Kai", false)];

    private void AddSimulatedFans(Room room)
    {
        MatchInfo? info = _host.Current?.Info.Match;
        foreach ((string name, bool home) in Fans)
        {
            var m = new Member
            {
                Id = "sim-" + name.ToLowerInvariant(), Name = name, Simulated = true,
                TeamId = info is null ? null : home ? info.Home.TeamId : info.Away.TeamId,
            };
            room.Members[m.Id] = m;
        }
    }

    private void Later(Room room, int minMs, int maxMs, Action act) =>
        room.Scheduled.Add((Now() + _rng.Next(minMs, maxMs), act));

    private void SimReact(Call c)
    {
        if (c.Kind is CallKind.Goal or CallKind.BigChance)
            lock (_sync)
                foreach (Room room in _rooms.Values.Where(r => r.Demo))
                {
                    var sims = room.Members.Values.Where(m => m.Simulated).OrderBy(_ => _rng.Next()).Take(c.Kind == CallKind.Goal ? 2 : 1).ToList();
                    foreach (Member fan in sims)
                        SimSay(room, fan, c.Kind == CallKind.BigChance ? ChanceLines : fan.TeamId == c.TeamId ? GoalLines : ConcededLines);
                }
        if (c.Kind is not (CallKind.Goal or CallKind.BigChance or CallKind.PredictionResult)) return;
        lock (_sync)
            foreach (Room room in _rooms.Values.Where(r => r.Demo))
                foreach (Member fan in room.Members.Values.Where(m => m.Simulated))
                {
                    if (_rng.NextDouble() < 0.4) continue;
                    bool ours = fan.TeamId == c.TeamId;
                    string emoji = c.Kind == CallKind.Goal ? (ours ? "🔥" : "😱") : c.Kind == CallKind.BigChance ? (ours ? "😱" : "👏") : "🤔";
                    string name = fan.Name, code = room.Code, id = fan.Id;
                    Later(room, 400, 2500, () => _ = _hub.Clients.Group(Group(code)).SendAsync("partyReaction", new PartyReaction(name, emoji, Now(), id)));
                }
        // After a goal, the scoring side of a demo room raises a tifo.
        if (c.Kind == CallKind.Goal && c.TeamId is { } scorers)
            lock (_sync)
                foreach (Room room in _rooms.Values.Where(r => r.Demo))
                {
                    Member? starter = room.Members.Values.Where(m => m.Simulated && m.TeamId == scorers).OrderBy(_ => _rng.Next()).FirstOrDefault();
                    if (starter is null) continue;
                    Room r = room;
                    string design = TifoDesigns[_rng.Next(TifoDesigns.Length)];
                    Later(room, 3000, 5000, () => { lock (_sync) StartTifoLocked(r, starter, scorers, design); });
                }
        // A demo room asks Second Ball IQ about each goal, so the presenter can show a room investigation.
        if (c.Kind == CallKind.Goal)
            lock (_sync)
                foreach (Room room in _rooms.Values.Where(r => r.Demo && r.SimQuestions < 3))
                {
                    Member asker = room.Members.Values.Where(m => m.Simulated).ElementAt(_rng.Next(Fans.Length));
                    room.SimQuestions++;
                    Room r = room;
                    Later(room, 4000, 7000, () => Add(r, asker, "How did that goal happen?", "en"));
                }
    }

    private void SimVote(Room room, Poll poll)
    {
        if (!room.Demo) return;
        foreach (Member fan in room.Members.Values.Where(m => m.Simulated))
        {
            double roll = _rng.NextDouble();
            string vote = poll.Options.Count == 2
                ? (roll < 0.6 ? poll.Options[0].Id : poll.Options[1].Id)
                : roll < 0.55 ? poll.Options[0].Id : roll < 0.85 ? poll.Options[1].Id : poll.Options[2].Id;
            string id = fan.Id;
            Later(room, 2000, (PollSeconds - 8) * 1000, () => { lock (_sync) if (!poll.Closed) poll.Votes[id] = vote; });
        }
        // After a tactical shift, someone asks the obvious question.
        if (poll.CandidateId.Length > 0 && poll.Question.Contains("switch", StringComparison.Ordinal) && room.SimQuestions < 3)
        {
            room.SimQuestions++;
            Member asker = room.Members.Values.Where(m => m.Simulated).ElementAt(_rng.Next(Fans.Length));
            Later(room, 6000, 10000, () => Add(room, asker, "Did they really change formation, or are they just pressing higher?", "en"));
        }
    }

    private void SimPick(Room room, Picks picks)
    {
        if (!room.Demo) return;
        IReadOnlyList<PredictionOption> options = picks.Prediction.Options;
        foreach (Member fan in room.Members.Values.Where(m => m.Simulated))
        {
            // Fans half follow the odds, half go with their gut.
            string pick = _rng.NextDouble() < 0.5 ? Weighted(options) : options[_rng.Next(options.Count)].Id;
            string id = fan.Id;
            Later(room, 300, 2200, () => { lock (_sync) if (picks.Outcome is null) picks.ByMember[id] = pick; });
        }
    }

    private void SimUpvote(Room room, Question q)
    {
        if (!room.Demo) return;
        foreach (Member fan in room.Members.Values.Where(m => m.Simulated && m.Id != q.ById).OrderBy(_ => _rng.Next()).Take(3))
        {
            string id = fan.Id;
            Later(room, 1500, 6000, () => { lock (_sync) if (q.Status == "open") q.Voters.Add(id); });
        }
    }

    private string Weighted(IReadOnlyList<PredictionOption> options)
    {
        double roll = _rng.NextDouble() * options.Sum(o => o.Probability);
        foreach (PredictionOption o in options)
        {
            roll -= o.Probability;
            if (roll <= 0) return o.Id;
        }
        return options[^1].Id;
    }

    // --------------------------------------------------------------------------------------------------------------
    // Views
    // --------------------------------------------------------------------------------------------------------------

    private Task Push(string? code)
    {
        if (code is null) return Task.CompletedTask;
        PartyState? state;
        lock (_sync) state = _rooms.TryGetValue(code, out Room? room) ? View(room) : null;
        return state is null ? Task.CompletedTask : _hub.Clients.Group(Group(code)).SendAsync("party", state);
    }

    private static PartyState View(Room room) => new(
        room.Code, room.Host,
        room.Members.Values.Where(m => m.Simulated || m.Connection is not null)
            .Select(m => new PartyMember(m.Id, m.Name, m.TeamId, m.Points, m.Simulated)).OrderByDescending(m => m.Points).ToList(),
        // The last four polls, plus any tactical test still waiting for the match to settle it.
        room.Polls.Where((p, i) => i >= room.Polls.Count - 4 || (p.TestId is not null && p.Outcome is null)).Select(p => new PartyPoll(p.Id, p.CandidateId, p.ClockSec, p.Question, p.QuestionFi,
            p.Options.Select(o => new PartyOption(o.Id, o.En, o.Fi, p.Votes.Values.Count(v => v == o.Id))).ToList(),
            p.ClosesAt, p.Closed, p.MatchLensPick, p.Verdict, p.VerdictFi, new Dictionary<string, string>(p.Votes),
            p.TestId, p.DeadlineSec, p.Outcome, p.Estimate)).ToList(),
        room.Picks.Values.TakeLast(4).Select(p => new PartyPicks(p.Prediction.PredictionId, p.Prediction.Kind.ToString(), p.Prediction.PlayerName,
            p.Prediction.Options.Select(o => new PartyOption(o.Id, o.Label, o.LabelFi, p.ByMember.Values.Count(v => v == o.Id))).ToList(),
            p.Prediction.Pick,
            p.ByMember.Count == 0 ? null : p.ByMember.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key,
            p.Outcome, p.Right.ToList(), new Dictionary<string, string>(p.ByMember))).ToList(),
        room.Questions.TakeLast(8).Select(q => new PartyQuestion(q.Id, q.Text, q.By, q.Language, q.Voters.Count, q.Status, q.Answer, q.At)).ToList(),
        room.Demo,
        room.Tifo is { } t && Now() < t.EndsAt + 2000
            ? new PartyTifo(t.Id, t.TeamId, t.Design, t.By, t.StartedAt, t.RevealAt, t.EndsAt, t.Holders.ToList(), Now())
            : null,
        room.Messages.TakeLast(MessagesKept).ToList());

    private (Room room, Member me)? Find(string connectionId)
    {
        lock (_sync)
        {
            if (!_roomOf.TryGetValue(connectionId, out string? code) || !_rooms.TryGetValue(code, out Room? room)) return null;
            Member? me = room.Members.Values.FirstOrDefault(m => m.Connection == connectionId);
            return me is null ? null : (room, me);
        }
    }

    private List<string> Codes()
    {
        lock (_sync) return _rooms.Keys.ToList();
    }

    private static string Group(string code) => "party:" + code.ToUpperInvariant();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Clean(string? s, int max = 20)
    {
        string t = Regex.Replace(s ?? "", @"[^\p{L}\p{N} '\-]", "").Trim();
        if (t.Length == 0) t = "Fan";
        return t.Length > max ? t[..max] : t;
    }

    public void Dispose() => _timer.Dispose();
}
