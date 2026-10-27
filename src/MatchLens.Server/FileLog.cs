using System.Diagnostics;
using System.Text;
using MatchLens.Decisions;
using Microsoft.Extensions.AI;

namespace MatchLens.Server;

/// <summary>
/// The server's log, also written to a file (logs/server-yyyyMMdd-HHmmss.log under the folder the server was started
/// from, or MATCHLENS_LOG_DIR), so a test run can be read back afterwards. Same filters as the console
/// (appsettings.json); one line per entry, exceptions in full. Never logs keys or endpoints' secrets.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();

    public string Path { get; }

    /// <summary>
    /// logs/ at the repository root: the folder above src/ when the server is started from its project folder (as
    /// Visual Studio does), otherwise logs/ in the folder it was started from.
    /// </summary>
    public static string DefaultDirectory()
    {
        string cwd = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(cwd);
        if (dir.Name == "MatchLens.Server" && dir.Parent is { Name: "src", Parent: { } root }) return System.IO.Path.Combine(root.FullName, "logs");
        return System.IO.Path.Combine(cwd, "logs");
    }

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, $"server-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, Short(categoryName));

    private static string Short(string category) => category.StartsWith("MatchLens.", StringComparison.Ordinal) ? category[10..] : category;

    internal void Write(string line)
    {
        lock (_sync) _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_sync) _writer.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string level = logLevel switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
            };
            var line = new StringBuilder($"{DateTime.Now:HH:mm:ss.fff} {level} {category}: {formatter(state, exception)}");
            if (exception is not null) line.Append(Environment.NewLine).Append(exception);
            owner.Write(line.ToString());
        }
    }
}

/// <summary>
/// Logs every Decision-1 call: which questions, how long it took, and each answer with its probabilities (or the
/// failure). The state text is not logged in full, only its length and its first words, to keep lines readable.
/// </summary>
public sealed class LoggingDecisionClient(IDecisionClient inner, ILogger log, ModelBudget? budget = null) : IDecisionClient
{
    public async Task<DecisionResult> DecideAsync(string state, IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        string names = string.Join(",", questions.Keys);
        if (budget is not null && !budget.TryTake(ModelKind.Decision))
        {
            log.LogWarning("Decision-1 [{Questions}] not called: the budget is spent for now", names);
            throw ModelBudgetDecisions.Exceeded();
        }
        string flat = state.ReplaceLineEndings(" ");
        string head = flat.Length > 90 ? flat[..90] + "…" : flat;
        try
        {
            DecisionResult result = await inner.DecideAsync(state, questions, cancellationToken);
            string answers = string.Join("; ", result.Answers.Select(a => $"{a.Key}={Describe(a.Value)}"));
            log.LogInformation("Decision-1 [{Questions}] {Ms} ms: {Answers} (state {Chars} chars: \"{Head}\")", names, watch.ElapsedMilliseconds, answers, state.Length, head);
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning("Decision-1 [{Questions}] failed after {Ms} ms: {Type}: {Message}", names, watch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    private static string Describe(DecisionAnswer answer) => answer switch
    {
        ChoiceAnswer c => $"{c.Choice} ({string.Join(" ", c.Probabilities.OrderByDescending(p => p.Value).Select(p => $"{p.Key}:{p.Value:0.00}"))})",
        YesNoAnswer y => $"yes {y.Probability:0.00}",
        RefusalAnswer => "refused",
        _ => answer.GetType().Name,
    };
}

/// <summary>
/// Logs every chat-model call: the agent (when the call names one), how long it took, the tokens and how it finished,
/// or the failure. Message contents are not logged.
/// </summary>
public sealed class LoggingChatClient(IChatClient inner, ILogger log, ModelBudget? budget = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Take();
        var watch = Stopwatch.StartNew();
        int count = messages.TryGetNonEnumeratedCount(out int n) ? n : -1;
        try
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
            log.LogInformation("Chat {Ms} ms: {Messages} messages in, tokens {In}/{Out}, finish {Finish}{Tools}",
                watch.ElapsedMilliseconds, count, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.FinishReason?.ToString() ?? "-",
                options?.Tools is { Count: > 0 } tools ? $", {tools.Count} tools offered" : "");
            return response;
        }
        catch (Exception ex)
        {
            log.LogWarning("Chat failed after {Ms} ms: {Type}: {Message}", watch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Take();
        var watch = Stopwatch.StartNew();
        int updates = 0;
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates++;
            yield return update;
        }
        log.LogInformation("Chat (streamed) {Ms} ms: {Updates} updates", watch.ElapsedMilliseconds, updates);
    }

    private void Take()
    {
        if (budget is null || budget.TryTake(ModelKind.Chat)) return;
        log.LogWarning("Chat not called: the budget is spent for now");
        throw new ModelBudgetExceededException(ModelKind.Chat);
    }
}
