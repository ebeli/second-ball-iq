using MatchLens.Decisions;

namespace MatchLens.Server;

public enum ModelKind { Chat, Decision, Speech }

/// <summary>
/// A spending cap for a public site: how many chat-model, Decision-1 and speech calls the server may make per hour and
/// per day (UTC). When a cap is reached the models pause until the window turns: matches go on with offline templates
/// (every number and check still works), the watch parties screen on the word list, and the studio shows
/// "Models paused". Limits come from the environment; 0 means no cap.
/// </summary>
public sealed class ModelBudget
{
    private readonly object _sync = new();
    private readonly Counter[] _counters;
    private readonly Func<DateTimeOffset> _clock;

    public ModelBudget(Func<DateTimeOffset>? clock = null, Func<string, string?>? env = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        env ??= Environment.GetEnvironmentVariable;
        // One judge-mode run (38' to full time) made about 190 chat calls and 170 Decision-1 calls; the defaults allow
        // about four runs an hour and twenty a day.
        _counters =
        [
            new(ModelKind.Chat, Limit(env, "MATCHLENS_MAX_CHAT_PER_HOUR", 800), Limit(env, "MATCHLENS_MAX_CHAT_PER_DAY", 4000)),
            new(ModelKind.Decision, Limit(env, "MATCHLENS_MAX_DECISION_PER_HOUR", 1000), Limit(env, "MATCHLENS_MAX_DECISION_PER_DAY", 5000)),
            new(ModelKind.Speech, Limit(env, "MATCHLENS_MAX_SPEECH_PER_HOUR", 300), Limit(env, "MATCHLENS_MAX_SPEECH_PER_DAY", 1500)),
        ];
    }

    /// <summary>Raised once when the models pause and once when they resume.</summary>
    public event Action<bool>? PausedChanged;

    private bool _wasPaused;

    /// <summary>Takes one call from the budget; false when a cap is reached (the call must not be made).</summary>
    public bool TryTake(ModelKind kind)
    {
        bool ok;
        lock (_sync)
        {
            Counter c = Get(kind);
            Roll(c);
            ok = (c.HourLimit <= 0 || c.Hour < c.HourLimit) && (c.DayLimit <= 0 || c.Day < c.DayLimit);
            if (ok) { c.Hour++; c.Day++; }
        }
        Notify();
        return ok;
    }

    /// <summary>True while the chat model or Decision-1 has reached a cap: new work runs offline.</summary>
    public bool Paused
    {
        get
        {
            bool paused;
            lock (_sync) paused = Spent(Get(ModelKind.Chat)) || Spent(Get(ModelKind.Decision));
            return paused;
        }
    }

    public bool SpeechPaused { get { lock (_sync) return Spent(Get(ModelKind.Speech)); } }

    /// <summary>What has been used against each cap, for /api/health.</summary>
    public object Report()
    {
        lock (_sync)
        {
            foreach (Counter c in _counters) Roll(c);
            DateTimeOffset now = _clock();
            return new
            {
                paused = Spent(Get(ModelKind.Chat)) || Spent(Get(ModelKind.Decision)),
                resumesAt = Spent(Get(ModelKind.Chat)) || Spent(Get(ModelKind.Decision)) ? ResumeAt(now) : (DateTimeOffset?)null,
                calls = _counters.ToDictionary(c => c.Kind.ToString().ToLowerInvariant(), c => new
                {
                    hour = c.Hour, hourLimit = c.HourLimit, day = c.Day, dayLimit = c.DayLimit,
                }),
            };
        }
    }

    private DateTimeOffset ResumeAt(DateTimeOffset now)
    {
        bool dayCap = _counters.Where(c => c.Kind != ModelKind.Speech).Any(c => c.DayLimit > 0 && c.Day >= c.DayLimit);
        return dayCap ? new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)
            : new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour + 1), TimeSpan.Zero);
    }

    private void Notify()
    {
        bool paused = Paused;
        bool changed;
        lock (_sync) { changed = paused != _wasPaused; _wasPaused = paused; }
        if (changed) PausedChanged?.Invoke(paused);
    }

    private Counter Get(ModelKind kind) => _counters[(int)kind];

    private bool Spent(Counter c)
    {
        Roll(c);
        return (c.HourLimit > 0 && c.Hour >= c.HourLimit) || (c.DayLimit > 0 && c.Day >= c.DayLimit);
    }

    private void Roll(Counter c)
    {
        DateTime now = _clock().UtcDateTime;
        long hour = now.Ticks / TimeSpan.TicksPerHour, day = now.Ticks / TimeSpan.TicksPerDay;
        if (c.HourKey != hour) { c.HourKey = hour; c.Hour = 0; }
        if (c.DayKey != day) { c.DayKey = day; c.Day = 0; }
    }

    private static int Limit(Func<string, string?> env, string name, int fallback) =>
        int.TryParse(env(name), out int value) ? value : fallback;

    private sealed class Counter(ModelKind kind, int hourLimit, int dayLimit)
    {
        public ModelKind Kind { get; } = kind;
        public int HourLimit { get; } = hourLimit;
        public int DayLimit { get; } = dayLimit;
        public long HourKey = -1, DayKey = -1;
        public int Hour, Day;
    }
}

/// <summary>Thrown instead of a chat call when the budget is spent; every agent already falls back on a failure.</summary>
public sealed class ModelBudgetExceededException(ModelKind kind)
    : Exception($"The {kind.ToString().ToLowerInvariant()} model budget is spent for now; running on templates.");

public static class ModelBudgetDecisions
{
    /// <summary>Decision-1's own failure type, so the gates and the predictor fall back exactly as on a timeout.</summary>
    public static DecisionException Exceeded() => new("The Decision-1 budget is spent for now; running on templates.", null);
}
