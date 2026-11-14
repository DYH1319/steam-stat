using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ElectronNet.Features.Assistant;
using ElectronNet.Infrastructure;
using Microsoft.Extensions.Logging;
using SteamStat.Contracts.Ipc;
using SteamStat.Core.Assistant;
using SteamStat.Core.Events;
using SteamStat.Core.Insights;
using SteamStat.Core.Settings;

namespace ElectronNet.Services;

internal sealed record AssistantProgressChanged(AssistantProgressDto Progress);

/// <summary>
/// AI（自带 Key）：问档案、写周刊社论、章节命名、报告开场白、防剧透前情提要。
/// <list type="bullet">
/// <item>默认关闭；没开、没 Key（Ollama 除外）或没填模型时什么都不发。</item>
/// <item>发出去的内容受分级授权约束：问档案靠工具的分级和语义视图网关，写作类请求把没授权的游戏和好友换成代号。</item>
/// <item>主播模式下好友一律不出门，SteamID 一律换成代号；隐藏游戏在视图里已经剔除。</item>
/// <item>每个请求体发出前原样写进审计日志。</item>
/// </list>
/// </summary>
internal sealed class AssistantService(
    ISettingsStore settingsStore,
    AssistantKeyStore keys,
    AuditLog audit,
    ArchiveToolCatalog tools,
    IHttpClientFactory httpClientFactory,
    WeeklyService weekly,
    MemoirService memoir,
    MemoirJournalService journal,
    SemanticLayerService semantic,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<AssistantService> logger) : IEventHandler<WeeklyIssuePublished>
{
    internal const string HttpClientName = "SteamStat.Assistant";
    private const int MaxHistoryTurns = 12;
    private const int MaxToolContentChars = 24_000;
    private static readonly TimeSpan AskTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    private AssistantSettings Settings => settingsStore.GetSettings().Assistant ?? new AssistantSettings();

    private static string ProviderOf(AssistantSettings settings)
        => AssistantProviders.IsValid(settings.Provider) ? settings.Provider! : AssistantProviders.Anthropic;

    private static string BaseUrlOf(AssistantSettings settings)
        => string.IsNullOrWhiteSpace(settings.BaseUrl) ? AssistantProviders.DefaultBaseUrl(ProviderOf(settings)) : settings.BaseUrl.Trim();

    private IReadOnlySet<string> EffectiveScopes()
    {
        var settings = settingsStore.GetSettings();
        return ExternalScopes.Effective(settings.Assistant?.Scopes, settings.StreamerMode == true);
    }

    public AssistantStatusDto GetStatus()
    {
        var settings = Settings;
        var provider = ProviderOf(settings);
        var hasKey = keys.Has(provider);
        var keyRequired = AssistantProviders.RequiresKey(provider);
        return new AssistantStatusDto
        {
            Enabled = settings.Enabled == true,
            Provider = provider,
            Model = settings.Model ?? string.Empty,
            EffectiveBaseUrl = BaseUrlOf(settings),
            HasKey = hasKey,
            KeyRequired = keyRequired,
            CanPersistKey = keys.CanPersist,
            Scopes = ExternalScopes.Normalize(settings.Scopes),
            EffectiveScopes = ExternalScopes.All.Where(EffectiveScopes().Contains).ToList(),
            Ready = Readiness(settings) == null
        };
    }

    public AssistantStatusDto SetKey(AssistantKeySetRequest request)
    {
        var key = request.Key.Length == 0 ? string.Empty : IpcSecret.Decode(request.Key) ?? throw new ArgumentException("The key must be s1: encoded.");
        keys.Set(ProviderOf(Settings), key);
        return GetStatus();
    }

    /// <summary>没准备好时返回状态码；准备好了返回 null。</summary>
    private string? Readiness(AssistantSettings settings, bool requireModel = true)
    {
        if (settings.Enabled != true) return "disabled";
        var provider = ProviderOf(settings);
        if (AssistantProviders.RequiresKey(provider) && !keys.Has(provider)) return "noKey";
        if (requireModel && string.IsNullOrWhiteSpace(settings.Model)) return "noModel";
        return null;
    }

    private IChatModel CreateModel(AssistantSettings settings, string kind, string summary, IReadOnlySet<string> scopes, Action? onRequest = null)
    {
        var provider = ProviderOf(settings);
        var baseUrl = BaseUrlOf(settings);
        var destination = $"{Host(baseUrl)} · {settings.Model}";
        var orderedScopes = ExternalScopes.All.Where(scopes.Contains).ToList();
        void Record(string body)
        {
            audit.Append(AuditChannels.Ai, kind, destination, summary, orderedScopes, body);
            onRequest?.Invoke();
        }
        var http = httpClientFactory.CreateClient(HttpClientName);
        return provider == AssistantProviders.Anthropic
            ? new AnthropicChatModel(http, baseUrl, keys.Get(provider) ?? string.Empty, Record)
            : new OpenAiChatModel(http, baseUrl, keys.Get(provider), Record);
    }

    private static string Host(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : baseUrl;

    private static string StatusOf(ChatModelException exception) => exception.Kind switch
    {
        ChatFailureKind.Unauthorized => "unauthorized",
        ChatFailureKind.Unreachable => "unreachable",
        _ => "failed"
    };

    public async Task<AssistantModelsDto> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        if (Readiness(settings, requireModel: false) is { } status) return new AssistantModelsDto { Status = status, Models = [] };
        var model = CreateModel(settings, "models", "GET models", new HashSet<string>());
        // GET 没有请求体，也记一笔：发出去的只有 Key（在请求头里，不写进日志）
        audit.Append(AuditChannels.Ai, "models", $"{Host(BaseUrlOf(settings))}", "GET models", [], $"GET {BaseUrlOf(settings)} (models)");
        try
        {
            return new AssistantModelsDto { Status = "ok", Models = await model.ListModelsAsync(cancellationToken).ConfigureAwait(false) };
        }
        catch (ChatModelException exception)
        {
            logger.LogInformation("Listing assistant models failed: {Kind} {Status}", exception.Kind, exception.StatusCode);
            return new AssistantModelsDto { Status = StatusOf(exception), Models = [] };
        }
    }

    // ------------------------------------------------------------------
    // 问档案
    // ------------------------------------------------------------------

    public void Cancel(string requestId)
    {
        if (_running.TryGetValue(requestId, out var source)) source.Cancel();
    }

    public async Task<AssistantAnswerDto> AskAsync(AssistantAskRequest request, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        var model = settings.Model ?? string.Empty;
        if (Readiness(settings) is { } notReady) return Answer(notReady, model);
        var question = IpcSecret.Decode(request.Question)?.Trim();
        if (string.IsNullOrEmpty(question) || question.Length > 2000) throw new ArgumentException("The question is empty or too long.");
        var chinese = request.Language == "zh-CN";
        var scopes = EffectiveScopes();
        var context = new ToolContext(scopes, maskIds: true, maxRows: 100, AccountSettings.MineOf(settingsStore.GetSettings()), chinese);
        var available = tools.Available(scopes);

        var messages = new List<ChatMessage>();
        foreach (var turn in (request.History ?? []).TakeLast(MaxHistoryTurns))
        {
            var text = IpcSecret.Decode(turn.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;
            messages.Add(turn.Role == "assistant" ? new AssistantMessage(Truncate(text, 8000), []) : new UserMessage(Truncate(text, 8000)));
        }
        messages.Add(new UserMessage(question));

        var evidence = new List<AssistantEvidenceDto>();
        var requests = 0;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(AskTimeout);
        _running[request.RequestId] = source;
        try
        {
            var chat = CreateModel(settings, "ask", question, scopes, () => Interlocked.Increment(ref requests));
            var progress = new Progress<AgentProgress>(step => _ = eventBus.PublishAsync(new AssistantProgressChanged(new AssistantProgressDto
            {
                RequestId = request.RequestId,
                Phase = step.Tool == null ? "thinking" : "tool",
                Tool = step.Tool,
                Step = step.Step
            })));
            var result = await AgentLoop.RunAsync(
                chat,
                new ChatRequest(model, AskSystemPrompt(scopes, chinese), messages, available.Select(ArchiveToolCatalog.Spec).ToList(), 2048),
                async (call, token) =>
                {
                    var table = await tools.ExecuteAsync(call.Name, call.Arguments, context, token).ConfigureAwait(false);
                    var (content, truncated) = ForModel(table);
                    evidence.Add(ToEvidence(call, table, truncated));
                    return new ToolOutcome(content, table.Error != null);
                },
                progress,
                AgentLoop.DefaultMaxSteps,
                source.Token).ConfigureAwait(false);
            return new AssistantAnswerDto
            {
                Status = result.HitLimit ? "limit" : "ok",
                Text = result.Text,
                Evidence = evidence,
                Model = model,
                Requests = result.Requests
            };
        }
        catch (ChatModelException exception)
        {
            logger.LogInformation("Assistant request failed: {Kind} {Status}", exception.Kind, exception.StatusCode);
            return Answer(StatusOf(exception), model, evidence, requests);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Answer(source.IsCancellationRequested ? "cancelled" : "failed", model, evidence, requests);
        }
        finally
        {
            _running.TryRemove(request.RequestId, out _);
        }
    }

    private static AssistantAnswerDto Answer(string status, string model, IReadOnlyList<AssistantEvidenceDto>? evidence = null, int requests = 0) => new()
    {
        Status = status,
        Text = string.Empty,
        Evidence = evidence ?? [],
        Model = model,
        Requests = requests
    };

    /// <summary>工具结果交给模型前限制大小：从尾部去行，直到装得下。</summary>
    internal static (string Content, bool Truncated) ForModel(ToolTable table)
    {
        var json = table.ToJson().ToJsonString();
        if (json.Length <= MaxToolContentChars || table.Error != null) return (json, table.Truncated);
        var rows = table.Rows.ToList();
        while (rows.Count > 1 && json.Length > MaxToolContentChars)
        {
            rows.RemoveRange(rows.Count / 2, rows.Count - rows.Count / 2);
            json = (table with { Rows = rows, Truncated = true }).ToJson().ToJsonString();
        }
        return (json.Length > MaxToolContentChars ? json[..MaxToolContentChars] : json, true);
    }

    private static AssistantEvidenceDto ToEvidence(ToolCall call, ToolTable table, bool truncated) => new()
    {
        Tool = call.Name,
        Arguments = call.Arguments.ToJsonString(),
        Columns = table.Columns,
        RowsJson = System.Text.Json.JsonSerializer.Serialize(table.Rows.Take(500)),
        RowCount = table.Rows.Count,
        Truncated = truncated || table.Truncated,
        Error = table.Error,
        Chart = table.Chart == null ? null : new AssistantChartDto { Kind = table.Chart.Kind, X = table.Chart.X, Y = table.Chart.Y }
    };

    private string AskSystemPrompt(IReadOnlySet<string> scopes, bool chinese)
    {
        var today = tools.Today;
        var boundary = Math.Clamp(settingsStore.GetSettings().Analytics?.DayBoundaryHour ?? 4, 0, 6);
        var weekday = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek;
        return $"""
            You are the archive assistant inside Steam Stat, a desktop app that keeps a local archive of the user's Steam play history.
            Answer the user's question with the read-only tools. Rules:
            - Only use numbers that appear in tool results. Never invent data. If the tools cannot answer, say so plainly.
            - Prefer hours to seconds. Times are in the user's local time zone. "Days" are logical days: the user's day starts at {boundary}:00.
            - Authorized scopes: {string.Join(", ", ExternalScopes.All.Where(scopes.Contains))}. Data outside them is not available to you. If a question needs another scope (games, notes or friends), say which one; the user can grant it in Steam Stat settings under "AI & local API".
            - Player identifiers are aliases such as player-1. Never guess real identities.
            - Be concise. Use Markdown: short paragraphs, bullet lists, small tables. Do not paste whole tool outputs; the app shows every tool result as evidence under your answer.
            - Today is {today} ({weekday}).
            Write the answer in {(chinese ? "Simplified Chinese" : "English")}.
            """;
    }

    // ------------------------------------------------------------------
    // 写作
    // ------------------------------------------------------------------

    private async Task<(string Status, string? Text)> WriteAsync(
        string kind, string summary, string system, string prompt, IReadOnlySet<string> scopes, int maxTokens, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (Readiness(settings) is { } notReady) return (notReady, null);
        try
        {
            var model = CreateModel(settings, kind, summary, scopes);
            var response = await model.CompleteAsync(new ChatRequest(settings.Model!, system, [new UserMessage(prompt)], [], maxTokens), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.Text) ? ("failed", null) : ("ok", response.Text.Trim());
        }
        catch (ChatModelException exception)
        {
            logger.LogInformation("Assistant {Kind} failed: {Failure} {Status}", kind, exception.Kind, exception.StatusCode);
            return (StatusOf(exception), null);
        }
    }

    private static string Voice(bool chinese) => chinese ? "Simplified Chinese" : "English";

    private IReadOnlySet<int> HiddenAppIds() => (settingsStore.GetSettings().Analytics?.HiddenAppIds ?? []).ToHashSet();

    public async Task<AssistantWriteResultDto> WriteWeeklyEditorialAsync(int issueId, CancellationToken cancellationToken = default)
    {
        var settings = Settings;
        if (Readiness(settings) is { } notReady) return new AssistantWriteResultDto { Status = notReady };
        var issue = await weekly.GetPayloadAsync(issueId, cancellationToken).ConfigureAwait(false);
        if (issue == null || issue.Value.Kind != "weekly") return new AssistantWriteResultDto { Status = "empty" };
        if (JsonNode.Parse(issue.Value.Payload) is not JsonObject payload) return new AssistantWriteResultDto { Status = "empty" };
        if (payload["editorial"] != null) return new AssistantWriteResultDto { Status = "exists" };

        var scopes = EffectiveScopes();
        var names = new Placeholders(scopes.Contains(ExternalScopes.Games), scopes.Contains(ExternalScopes.Friends), HiddenAppIds());
        var facts = WeeklyFacts(payload, names, scopes.Contains(ExternalScopes.Friends));
        var system = """
            You write the editorial column of a personal gaming newspaper about one person (always called "the subject"; in Chinese 当事人).
            Voice: deadpan, dry humour. Report absurd facts with a perfectly straight face. Never mock, never moralise, never give advice about playing less.
            Use only the facts you are given. Keep placeholder tokens such as {G1} or {F2} exactly as written and do not invent new ones.
            Reply with JSON only: {"zh": "<Chinese, 120-200 characters>", "en": "<English, 80-130 words>"}.
            """;
        var (status, text) = await WriteAsync("weeklyEditorial", $"Weekly issue #{payload["number"]}", system,
            $"{facts}\n\n{names.Legend(false)}", scopes, 1200, cancellationToken).ConfigureAwait(false);
        if (status != "ok") return new AssistantWriteResultDto { Status = status };
        var json = ModelOutput.ExtractJson(text);
        var zh = names.ToWeeklyText(ModelOutput.CleanLine(Str(json?["zh"]), 600));
        var en = names.ToWeeklyText(ModelOutput.CleanLine(Str(json?["en"]), 1500));
        if (zh.Length == 0 && en.Length == 0) return new AssistantWriteResultDto { Status = "failed" };
        var saved = await weekly.SetEditorialAsync(issueId,
            new WeeklyEditorial(new Bilingual(zh.Length > 0 ? zh : en, en.Length > 0 ? en : zh), settings.Model!, timeProvider.GetUtcNow().ToUnixTimeSeconds()),
            cancellationToken).ConfigureAwait(false);
        return new AssistantWriteResultDto { Status = saved ? "ok" : "exists", Text = zh };
    }

    /// <summary>周刊里的事实，文字里的游戏和好友换成代号。好友没授权时整版不发。</summary>
    internal static string WeeklyFacts(JsonObject payload, Placeholders names, bool friends)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Issue #{payload["number"]}, {Str(payload["fromDay"])} to {Str(payload["toDay"])}.");
        if (payload["quiet"]?.GetValue<bool>() == true) builder.AppendLine("Nothing was played this week.");
        if (payload["numbers"] is JsonObject numbers)
        {
            var hours = Math.Round((numbers["seconds"]?.GetValue<double>() ?? 0) / 3600, 1);
            var previous = Math.Round((numbers["previousSeconds"]?.GetValue<double>() ?? 0) / 3600, 1);
            builder.AppendLine($"Numbers: {hours} hours (previous week {previous}), {numbers["sessions"]} sessions, {numbers["games"]} games, {numbers["activeDays"]} active days, {numbers["achievements"]} achievements, {numbers["newGames"]} new games, {numbers["lateNight"]} late-night sessions.");
        }
        void Story(string label, JsonNode? story)
        {
            if (story is not JsonObject item) return;
            builder.AppendLine($"{label}: {Text(item["title"])} — {Text(item["body"])}");
        }
        string Text(JsonNode? bilingual) => names.FromWeeklyText($"{Str(bilingual?["en"])} / {Str(bilingual?["zh"])}");
        Story("Headline", payload["headline"]);
        foreach (var brief in payload["briefs"] as JsonArray ?? []) Story("Brief", brief);
        foreach (var observation in payload["observations"] as JsonArray ?? []) builder.AppendLine($"Observation: {Text(observation)}");
        if (payload["schedule"] is JsonObject schedule) builder.AppendLine($"Schedule: {Text(schedule["text"])}");
        if (payload["market"] is JsonObject market) builder.AppendLine($"Market: {Text(market["commentary"])}");
        foreach (var obituary in (payload["obituaries"] as JsonArray ?? []).OfType<JsonObject>())
            builder.AppendLine($"Obituary: {names.Game(obituary["appId"]?.GetValue<int>() ?? 0, Str(obituary["name"]) ?? "")}, {obituary["hours"]} hours, idle {obituary["idleDays"]} days.");
        if (payload["forecast"] is JsonObject forecast) builder.AppendLine($"Forecast: {Text(forecast["summary"])}");
        if (friends && payload["friends"]?["items"] is JsonArray items)
            foreach (var item in items) builder.AppendLine($"Friends: {Text(item)}");
        return builder.ToString().TrimEnd();
    }

    public async Task<AssistantChapterNamesDto> NameChaptersAsync(AssistantChapterNamesRequest request, CancellationToken cancellationToken = default)
    {
        if (Readiness(Settings) is { } notReady) return new AssistantChapterNamesDto { Status = notReady, Names = [] };
        var chapters = await memoir.GetChaptersAsync(new ArchiveScopeRequest { SteamIds = request.SteamIds }, cancellationToken).ConfigureAwait(false);
        if (chapters.Chapters.Count == 0) return new AssistantChapterNamesDto { Status = "empty", Names = [] };

        var chinese = request.Language == "zh-CN";
        var scopes = EffectiveScopes();
        var names = new Placeholders(scopes.Contains(ExternalScopes.Games), false, HiddenAppIds());
        var builder = new StringBuilder();
        foreach (var chapter in chapters.Chapters)
        {
            var top = string.Join(", ", chapter.TopGames.Take(4).Select(game => $"{names.Game(game.AppId, game.Name)} {Math.Round(game.Share * 100)}%"));
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{chapter.Index}: {chapter.FromMonth}..{chapter.ToMonth} ({chapter.Months} months), pattern {chapter.Kind}, {Math.Round(chapter.Seconds / 3600.0)} h total, {Math.Round(chapter.MonthlySeconds / 3600.0)} h/month, peak {chapter.PeakMonth}, {chapter.NewGames} new games, {chapter.Unlocks} achievements{(chapter.NightShare is { } night ? $", {Math.Round(night * 100)}% late-night" : "")}; top: {top}");
        }
        var system = $"""
            You name the chapters of a person's gaming life for a memoir. Each title: evocative, specific, a little poetic; at most {(chinese ? "12 Chinese characters" : "6 English words")}.
            Patterns: era = one dominant game, duo = two games, mostly = one game leads, sampler = many short tries, variety = many games, quiet = little play.
            Placeholder tokens such as {"{G1}"} stand for games; you may use them and must keep them exactly as written.
            Write in {Voice(chinese)}. Reply with JSON only: [{"{"}"index": 0, "title": "..."{"}"}].
            """;
        var (status, text) = await WriteAsync("chapterNames", $"{chapters.Chapters.Count} chapters", system,
            $"{builder.ToString().TrimEnd()}\n\n{names.Legend(chinese)}", scopes, 1200, cancellationToken).ConfigureAwait(false);
        if (status != "ok") return new AssistantChapterNamesDto { Status = status, Names = [] };
        var titles = (ModelOutput.ExtractJson(text) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(item => (Index: item["index"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : -1, Title: names.ToPlainText(ModelOutput.CleanLine(Str(item["title"]), 40))))
            .Where(item => item.Title.Length > 0)
            .ToDictionary(item => item.Index, item => item.Title);
        var result = chapters.Chapters
            .Where(chapter => titles.ContainsKey(chapter.Index))
            .Select(chapter => new AssistantChapterNameDto { FromMonth = chapter.FromMonth, ToMonth = chapter.ToMonth, Title = titles[chapter.Index] })
            .ToList();
        return new AssistantChapterNamesDto { Status = result.Count == 0 ? "failed" : "ok", Names = result };
    }

    public async Task<AssistantWriteResultDto> WriteRecapAsync(AssistantRecapRequest request, CancellationToken cancellationToken = default)
    {
        if (Readiness(Settings) is { } notReady) return new AssistantWriteResultDto { Status = notReady };
        var chinese = request.Language == "zh-CN";
        var scopes = EffectiveScopes();
        var hidden = HiddenAppIds();
        var names = new Placeholders(scopes.Contains(ExternalScopes.Games), false, hidden);
        var period = IpcSecret.Decode(request.Period) ?? string.Empty;
        var topGames = (request.TopGames ?? [])
            .Where(game => !hidden.Contains(game.AppId))
            .Take(5)
            .Select(game => (game.AppId, Name: IpcSecret.Decode(game.Name) ?? string.Empty, game.Seconds))
            .ToList();
        var games = topGames.Select(game => $"{names.Game(game.AppId, game.Name)} {Math.Round(game.Seconds / 3600.0, 1)} h").ToList();
        var facts = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Recap: {request.Unit} — {period}.")
            .AppendLine(CultureInfo.InvariantCulture, $"{Math.Round(request.Seconds / 3600.0, 1)} hours (previous {request.Unit}: {Math.Round(request.PreviousSeconds / 3600.0, 1)}), {request.Sessions} sessions, {request.Games} games, {request.ActiveDays} active days, {request.NewGames} new games.");
        if (request.LongestSessionSeconds is { } longest)
        {
            // 最长一局的游戏只在前五名里才知道名字，不在就不提是哪款
            var longestGame = topGames.FirstOrDefault(game => game.AppId == request.LongestSessionAppId);
            facts.AppendLine(CultureInfo.InvariantCulture, $"Longest session: {Math.Round(longest / 3600.0, 1)} h{(longestGame.Name is { Length: > 0 } ? $" in {names.Game(longestGame.AppId, longestGame.Name)}" : "")}.");
        }
        if (request.StreakDays is { } streak) facts.AppendLine(CultureInfo.InvariantCulture, $"Longest streak: {streak} days.");
        if (request.LatestNightMinutes is { } night) facts.AppendLine(CultureInfo.InvariantCulture, $"Latest night: until {night / 60 % 24:00}:{night % 60:00}.");
        if (games.Count > 0) facts.AppendLine($"Top games: {string.Join(", ", games)}.");
        var system = $"""
            You write the opening line on the cover slide of a person's {request.Unit}ly gaming recap: one or two sentences, warm and a little witty, addressed to the player as "you".
            At most {(chinese ? "60 Chinese characters" : "35 English words")}. Use only the facts given; no advice about playing less.
            Placeholder tokens such as {"{G1}"} stand for games; keep them exactly as written.
            Write in {Voice(chinese)}. Reply with the line only.
            """;
        var (status, text) = await WriteAsync("recap", $"{request.Unit} {period}", system, $"{facts.ToString().TrimEnd()}\n\n{names.Legend(chinese)}", scopes, 300, cancellationToken).ConfigureAwait(false);
        return status == "ok"
            ? new AssistantWriteResultDto { Status = "ok", Text = names.ToPlainText(ModelOutput.CleanLine(text, chinese ? 160 : 400)) }
            : new AssistantWriteResultDto { Status = status };
    }

    public async Task<AssistantWriteResultDto> WritePreviouslyOnAsync(AssistantPreviouslyOnRequest request, CancellationToken cancellationToken = default)
    {
        if (Readiness(Settings) is { } notReady) return new AssistantWriteResultDto { Status = notReady };
        var scopes = EffectiveScopes();
        // 前情提要离不开游戏名：没授权就不发，告诉界面缺哪一级
        if (!scopes.Contains(ExternalScopes.Games)) return new AssistantWriteResultDto { Status = "scope", MissingScope = ExternalScopes.Games };
        if (HiddenAppIds().Contains(request.AppId)) return new AssistantWriteResultDto { Status = "empty" };
        var recap = await journal.GetPreviouslyOnAsync(new PreviouslyOnRequest { AppId = request.AppId, SteamIds = request.SteamIds }, cancellationToken).ConfigureAwait(false);
        if (recap == null) return new AssistantWriteResultDto { Status = "empty" };

        var parameters = new Dictionary<string, object?> { ["$app"] = request.AppId };
        var scopeSql = string.Empty;
        if (request.SteamIds is { Count: > 0 } steamIds)
        {
            scopeSql = $" AND steam_id IN ({string.Join(", ", steamIds.Select((id, index) => { parameters[$"$sid{index}"] = id; return $"$sid{index}"; }))})";
        }
        var unlocks = await semantic.QueryAsync(
            $"SELECT name, description, unlock_time FROM v_achievement_unlock WHERE app_id = $app{scopeSql} ORDER BY unlock_time DESC LIMIT 60",
            scopes, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        var chinese = request.Language == "zh-CN";
        var facts = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Game: {recap.Name}. Returning after {recap.GapDays} days; {Math.Round(recap.TotalSeconds / 3600.0, 1)} hours played in total; last period: {Math.Round(recap.PeriodSeconds / 3600.0, 1)} hours over {recap.PeriodSessions} sessions.");
        if (recap.Progress is { } progress) facts.AppendLine(CultureInfo.InvariantCulture, $"Achievements: {progress.Unlocked} of {progress.Total}.");
        if (recap.Presence is { } presence) facts.AppendLine($"Last in-game status: {presence.Text}");
        if (recap.Status != null) facts.AppendLine($"User's status for the game: {recap.Status}");
        if (unlocks.Rows.Count > 0)
        {
            facts.AppendLine("Unlocked achievements (newest first):");
            foreach (var row in unlocks.Rows) facts.AppendLine($"- {row[0]}: {row[1]}");
        }
        if (scopes.Contains(ExternalScopes.Notes))
            foreach (var note in recap.Notes.Take(10)) facts.AppendLine($"User's note: {Truncate(note.Text, 500)}");

        var system = $"""
            The player is returning to a game after a long break. Write a short "previously on" recap (3 to 5 sentences) that helps them remember where they were.
            STRICT SPOILER BOUNDARY: only mention story, places, characters, mechanics or events that are evidenced by the unlocked achievements, the player's own notes or their last in-game status listed by the user. Never mention anything that happens later in the game, locked achievements, endings, twists or what comes next, even if you know the game well. When the evidence is thin, stay vague and talk about their progress instead.
            No headings, no lists. Address the player as "you". Write in {Voice(chinese)}.
            """;
        var (status, text) = await WriteAsync("previouslyOn", recap.Name, system, facts.ToString().TrimEnd(), scopes, 600, cancellationToken).ConfigureAwait(false);
        return status == "ok"
            ? new AssistantWriteResultDto { Status = "ok", Text = ModelOutput.CleanLine(text, 2000) }
            : new AssistantWriteResultDto { Status = status };
    }

    /// <summary>新一期周刊出版后自动写社论（设置里能关）。补印和号外不写。后台进行，失败只记日志。</summary>
    public Task HandleAsync(WeeklyIssuePublished message, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (message.Issue.Kind != "weekly" || message.Issue.Backfilled || settings.WeeklyEditorial != true || Readiness(settings) != null)
            return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await WriteWeeklyEditorialAsync(message.Issue.Id).ConfigureAwait(false);
                logger.LogInformation("Weekly editorial for issue {Number}: {Status}", message.Issue.Number, result.Status);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to write the weekly editorial");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
