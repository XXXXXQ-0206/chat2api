using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Chat2ApiTray.Models;
using Chat2ApiTray.Services;
using Chat2ApiTray.Services.Api;
using Chat2ApiTray.Services.Context;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;

const string MixedTextToolEnvelope = "Before the tool call. <chat2api_tool_calls>[{\"id\":\"call_mixed\",\"name\":\"lookup\",\"arguments\":{\"query\":\"status\"}}]</chat2api_tool_calls> After the tool call.";

var tests = new (string Name, Func<Task> Run)[]
{
    ("openai streaming splits text into fine grained chunks", OpenAiStreamingSplitsTextIntoFineGrainedChunks),
    ("OpenAI tool streams include stable tool indexes", OpenAiToolStreamsIncludeStableToolIndexes),
    ("Responses tool streams include output item indexes", ResponsesToolStreamsIncludeOutputItemIndexes),
    ("legacy stream helpers preserve text beside tool calls", LegacyStreamHelpersPreserveTextBesideToolCalls),
    ("OpenAI JSON preserves text beside tool calls", OpenAiJsonPreservesTextBesideToolCalls),
    ("Anthropic JSON preserves text beside tool calls", AnthropicJsonPreservesTextBesideToolCalls),
    ("Responses JSON preserves text beside tool calls", ResponsesJsonPreservesTextBesideToolCalls),
    ("file logger redacts credentials and prompt bodies", FileLoggerRedactsCredentialsAndPromptBodies),
    ("file logger includes exception stack traces", FileLoggerIncludesExceptionStackTraces),
    ("file logger rotates daily for retention", FileLoggerRotatesDailyForRetention),
    ("local data directories receive private ACLs", LocalDataDirectoriesReceivePrivateAcls),
    ("concurrent conversation writes preserve private data directories", ConcurrentConversationWritesPreservePrivateDataDirectories),
    ("data retention prunes expiring stores and preserves browser profile", DataRetentionPrunesExpiringStoresAndPreservesBrowserProfile),
    ("context engine appends assistant text result", ContextEngineAppendsAssistantTextResult),
    ("context engine appends assistant tool calls", ContextEngineAppendsAssistantToolCalls),
    ("context engine falls back when the vector index is unavailable", ContextEngineFallsBackWhenVectorIndexIsUnavailable),
    ("anthropic adapter preserves tool use and result content", AnthropicAdapterPreservesToolUseAndResultContent),
    ("responses adapter preserves function call output items", ResponsesAdapterPreservesFunctionCallOutputItems),
    ("protocol adapters preserve vision file ids", ProtocolAdaptersPreserveVisionFileIds),
    ("protocol policy defaults to expert and rejects fast", ProtocolPolicyDefaultsToExpertAndRejectsFast),
    ("web search controls use tool calls without browser search", WebSearchControlsUseToolCallsWithoutBrowserSearch),
    ("context probe store persists measured limits", ContextProbeStorePersistsMeasuredLimits),
    ("conversation store quarantines malformed state", ConversationStoreQuarantinesMalformedState),
    ("conversation store isolates ids with the same legacy file name", ConversationStoreIsolatesLegacyFileNameCollisions),
    ("conversation store retries transient replacement sharing violations", ConversationStoreRetriesTransientReplacementSharingViolations),
    ("response continuation store quarantines malformed state", ResponseContinuationStoreQuarantinesMalformedState),
    ("context probe store quarantines malformed state", ContextProbeStoreQuarantinesMalformedState),
    ("context probe runner writes real measured limit", ContextProbeRunnerWritesRealMeasuredLimit),
    ("context probe runner requires repeated explicit limit evidence", ContextProbeRunnerRequiresRepeatedExplicitLimitEvidence),
    ("context probe runner records operational failures as unknown", ContextProbeRunnerRecordsOperationalFailuresAsUnknown),
    ("context probe records an unbounded upper test range", ContextProbeRecordsUnboundedUpperTestRange),
    ("context engine applies persisted probe limit", ContextEngineAppliesPersistedProbeLimit),
    ("context engine ignores inconclusive probe limits", ContextEngineIgnoresInconclusiveProbeLimits),
    ("context engine continues standard client sessions without a private id", ContextEngineContinuesStandardClientSessionsWithoutPrivateId),
    ("context engine serializes concurrent conversation updates", ContextEngineSerializesConcurrentConversationUpdates),
    ("conversation gates serialize across engine instances", ConversationGatesSerializeAcrossEngineInstances),
    ("embedded host runs and persists context probes", EmbeddedHostRunsAndPersistsContextProbes),
    ("embedded host streams provider deltas over OpenAI SSE", EmbeddedHostStreamsProviderDeltas),
    ("embedded host streams Anthropic and Responses events", EmbeddedHostStreamsOtherProtocols),
    ("embedded host streams text before buffered tool events", EmbeddedHostStreamsTextBeforeBufferedToolEvents),
    ("embedded host terminates failed provider streams", EmbeddedHostTerminatesFailedProviderStreams),
    ("Responses previous response id survives host restart", ResponsesPreviousResponseIdSurvivesHostRestart),
    ("browser provider requires explicit login", BrowserProviderRequiresExplicitLogin),
    ("offline mode refuses every browser provider entry point", OfflineModeRefusesBrowserProvider),
    ("offline mode refuses non-mock provider aliases", OfflineModeRefusesNonMockProviderAliases),
    ("embedded host rejects non-loopback binding before adapter creation", EmbeddedHostRejectsNonLoopbackBinding),
    ("browser provider timeout returns gateway timeout", BrowserProviderTimeoutReturnsGatewayTimeout),
    ("embedded host rejects malformed JSON as an invalid request", EmbeddedHostRejectsMalformedJson),
    ("embedded host returns invalid tool envelope error", EmbeddedHostReturnsInvalidToolEnvelopeError),
    ("embedded host rejects invalid tool streams before sending SSE", EmbeddedHostRejectsInvalidToolStreamsBeforeSendingSse),
    ("embedded host rejects file paths outside data directory", EmbeddedHostRejectsExternalFilePath),
    ("embedded host returns uploaded file metadata", EmbeddedHostReturnsUploadedFileMetadata),
    ("embedded host forwards uploaded files to browser adapter", EmbeddedHostForwardsUploadedFilesToBrowserAdapter),
    ("embedded host promotes uploaded image files to vision", EmbeddedHostPromotesUploadedImageFilesToVision),
    ("sqlite vector index retrieves relevant history", SqliteVectorIndexRetrievesRelevantHistory),
    ("sqlite vector index chunks oversized history within retrieval budget", SqliteVectorIndexChunksOversizedHistoryWithinRetrievalBudget),
    ("context engine uses model incremental summarizer", ContextEngineUsesModelIncrementalSummarizer),
    ("delegate incremental summarizer forwards cancellation", DelegateIncrementalSummarizerForwardsCancellation),
    ("context engine falls back when model incremental summarizer fails", ContextEngineFallsBackWhenModelIncrementalSummarizerFails),
    ("context engine summarizes only newly evicted messages", ContextEngineSummarizesOnlyNewlyEvictedMessages),
    ("context engine summarizes managed overflow before trimming", ContextEngineSummarizesManagedOverflowBeforeTrimming),
    ("context engine indexes managed overflow for semantic retrieval", ContextEngineIndexesManagedOverflowForSemanticRetrieval),
    ("context engine rejects an oversized current message before provider work", ContextEngineRejectsOversizedCurrentMessageBeforeProviderWork),
    ("context engine chunks oversized summary inputs", ContextEngineChunksOversizedSummaryInputs),
    ("embedded host delegates incremental summaries to web adapter", EmbeddedHostDelegatesIncrementalSummariesToWebAdapter),
    ("browser host probe delegates to web adapter and persists result", BrowserHostProbeDelegatesToWebAdapter),
    ("browser context probe bounds provider timeout", BrowserContextProbeBoundsProviderTimeout),
    ("embedded host starts login through its adapter", EmbeddedHostStartsLoginThroughAdapter),
    ("browser requests switch headed login contexts to headless", BrowserRequestsSwitchHeadedLoginContextsToHeadless),
    ("browser mode selector targets DeepSeek radio controls", BrowserModeSelectorTargetsDeepSeekRadioControls),
    ("browser composer uses enabled DeepSeek send control", BrowserComposerUsesEnabledDeepSeekSendControl),
    ("provider streaming pipeline forwards observed deltas", ProviderStreamingPipelineForwardsObservedDeltas),
    ("provider completion pipeline repairs malformed tool envelopes", ProviderCompletionPipelineRepairsMalformedToolEnvelopes),
    ("provider completion pipeline preserves text beside tool calls", ProviderCompletionPipelinePreservesTextBesideToolCalls),
    ("provider completion pipeline repairs missing required tool calls", ProviderCompletionPipelineRepairsMissingRequiredToolCalls),
    ("provider completion pipeline repairs Anthropic any tool calls", ProviderCompletionPipelineRepairsAnthropicAnyToolCalls),
    ("provider completion pipeline rejects tool calls when no tools are declared", ProviderCompletionPipelineRejectsUndeclaredToolEnvelope),
    ("provider completion pipeline rejects tool choice none envelopes without repair", ProviderCompletionPipelineRejectsToolChoiceNoneEnvelope),
    ("provider completion pipeline rejects missing required tool calls after repair", ProviderCompletionPipelineRejectsMissingRequiredToolCallsAfterRepair),
    ("provider completion pipeline rejects unrepaired tool envelopes", ProviderCompletionPipelineRejectsUnrepairedToolEnvelopes),
    ("provider completion pipeline rejects external tool schema references", ProviderCompletionPipelineRejectsExternalToolSchemaReferences),
    ("tool repair loop fixes malformed tool envelopes", ToolRepairLoopFixesMalformedToolEnvelopes),
    ("tool repair loop repairs undeclared tool calls", ToolRepairLoopRepairsUndeclaredToolCalls),
    ("tool repair loop repairs invalid tool arguments", ToolRepairLoopRepairsInvalidToolArguments),
    ("tool repair loop repairs declared schema violations", ToolRepairLoopRepairsDeclaredSchemaViolations),
    ("tool repair loop retries a failed repair", ToolRepairLoopRetriesFailedRepair)
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
        passed += 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {test.Name}");
        Console.Error.WriteLine(ex);
        Environment.ExitCode = 1;
        break;
    }
}

if (Environment.ExitCode == 0)
{
    Console.WriteLine($"All {passed} tests passed.");
}

static Task FileLoggerRedactsCredentialsAndPromptBodies()
{
    var root = CreateTempDirectory();
    var directory = Path.Combine(root, "Logs");
    var logger = new FileLogger(directory);
    logger.Info("Authorization: Bearer secret-token Cookie: session=super-secret prompt=do not persist this prompt");
    logger.Error(new InvalidOperationException("{\"api_key\":\"key-secret\"}"), "request body: do not persist this prompt");

    var content = File.ReadAllText(logger.LogFilePath);
    AssertTrue(!content.Contains("secret-token", StringComparison.Ordinal), "authorization redacted");
    AssertTrue(!content.Contains("super-secret", StringComparison.Ordinal), "cookie redacted");
    AssertTrue(!content.Contains("key-secret", StringComparison.Ordinal), "json key redacted");
    AssertTrue(!content.Contains("do not persist this prompt", StringComparison.Ordinal), "prompt redacted");
    AssertContains(content, "[REDACTED]", "redaction marker");
    return Task.CompletedTask;
}

static Task FileLoggerIncludesExceptionStackTraces()
{
    var root = CreateTempDirectory();
    var logger = new FileLogger(Path.Combine(root, "Logs"));
    Exception exception;
    try
    {
        ThrowLoggedException();
        throw new InvalidOperationException("unreachable");
    }
    catch (Exception captured)
    {
        exception = captured;
    }

    logger.Error(exception, "synthetic logger failure");
    var content = File.ReadAllText(logger.LogFilePath);
    AssertContains(content, "synthetic logger failure", "logger preserves error message");
    AssertContains(content, nameof(ThrowLoggedException), "logger preserves exception stack trace");
    return Task.CompletedTask;
}

static void ThrowLoggedException()
{
    throw new InvalidOperationException("synthetic exception");
}

static async Task FileLoggerRotatesDailyForRetention()
{
    var root = CreateTempDirectory();
    var directory = Path.Combine(root, "Logs");
    var current = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
    var logger = new FileLogger(directory, () => current);
    logger.Info("first day");
    current = current.AddDays(1);
    logger.Info("second day");

    var firstLog = Path.Combine(directory, "tray-20260701.log");
    var secondLog = Path.Combine(directory, "tray-20260702.log");
    AssertTrue(File.Exists(firstLog), "first daily log exists");
    AssertTrue(File.Exists(secondLog), "second daily log exists");
    AssertTrue(!File.Exists(Path.Combine(directory, "tray.log")), "legacy perpetual log is not created");

    File.SetLastWriteTimeUtc(firstLog, DateTime.UtcNow.AddDays(-8));
    var retention = await DataRetentionService.PruneAsync(root, new TraySettings { LogRetentionDays = 7 });
    AssertTrue(!File.Exists(firstLog), "expired rotated log is pruned");
    AssertTrue(File.Exists(secondLog), "active rotated log remains");
    AssertEqual(1, retention["Logs"], "expired rotated log retention count");
}

static Task LocalDataDirectoriesReceivePrivateAcls()
{
    if (!OperatingSystem.IsWindows())
    {
        return Task.CompletedTask;
    }

    var root = CreateTempDirectory();
    var conversations = Path.Combine(root, "Conversations");
    LocalDataDirectorySecurity.EnsurePrivateDirectory(root);
    LocalDataDirectorySecurity.EnsurePrivateDirectory(conversations);

    AssertPrivateAcl(root, "private root ACL");
    AssertPrivateAcl(conversations, "private child ACL");
    return Task.CompletedTask;
}

static async Task ConcurrentConversationWritesPreservePrivateDataDirectories()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    const int workers = 16;
    const int writesPerWorker = 4;
    var root = CreateTempDirectory();
    var store = new ConversationStore(root);
    using var barrier = new Barrier(workers);
    var writes = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("concurrent conversation writer barrier timed out");
        }

        for (var write = 0; write < writesPerWorker; write += 1)
        {
            await store.SaveAsync(new ConversationRecord
            {
                Id = $"concurrent-{worker}-{write}",
                Messages = [new ApiMessage("user", "synthetic concurrent state")]
            });
        }
    }));

    await Task.WhenAll(writes);
    var conversationDirectory = Path.Combine(root, "Conversations");
    AssertEqual(workers * writesPerWorker, Directory.EnumerateFiles(conversationDirectory, "*.json").Count(), "concurrent conversation file count");
    AssertPrivateAcl(conversationDirectory, "concurrent conversation directory ACL");
    var reloaded = await store.LoadAsync("concurrent-0-0");
    AssertEqual("synthetic concurrent state", reloaded.Messages.Single().Content, "concurrent conversation reload");
}

static void AssertPrivateAcl(string path, string name)
{
    var acl = new DirectoryInfo(path).GetAccessControl();
    AssertTrue(acl.AreAccessRulesProtected, $"{name} protects inherited ACLs");
    var allowedSids = new HashSet<string>(StringComparer.Ordinal)
    {
        WindowsIdentity.GetCurrent().User!.Value,
        "S-1-5-18",
        "S-1-5-32-544"
    };
    var readBits = FileSystemRights.ReadData
        | FileSystemRights.ReadAttributes
        | FileSystemRights.ReadExtendedAttributes;
    foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
    {
        if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & readBits) == 0)
        {
            continue;
        }

        var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
        AssertTrue(allowedSids.Contains(sid), $"{name} excludes broad read principal {sid}");
    }
}

static async Task DataRetentionPrunesExpiringStoresAndPreservesBrowserProfile()
{
    var dir = CreateTempDirectory();
    var oldUpload = Path.Combine(dir, "Uploads", "old.bin");
    var currentUpload = Path.Combine(dir, "Uploads", "current.bin");
    var oldConversation = Path.Combine(dir, "Conversations", "old.json");
    var browserState = Path.Combine(dir, "BrowserProfile", "Login Data");
    foreach (var path in new[] { oldUpload, currentUpload, oldConversation, browserState })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "test");
    }
    File.SetLastWriteTimeUtc(oldUpload, DateTime.UtcNow.AddDays(-8));
    File.SetLastWriteTimeUtc(oldConversation, DateTime.UtcNow.AddDays(-31));
    File.SetLastWriteTimeUtc(browserState, DateTime.UtcNow.AddDays(-365));

    var result = await DataRetentionService.PruneAsync(dir, new TraySettings());

    AssertTrue(!File.Exists(oldUpload), "expired upload deleted");
    AssertTrue(File.Exists(currentUpload), "current upload retained");
    AssertTrue(!File.Exists(oldConversation), "expired conversation deleted");
    AssertTrue(File.Exists(browserState), "browser profile retained until explicit user deletion");
    AssertEqual(1, result["Uploads"], "upload retention count");
    AssertEqual(1, result["Conversations"], "conversation retention count");
}

static async Task ContextEngineAppendsAssistantTextResult()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("agent-loop", [new ApiMessage("user", "create a hello.txt file")]);

    var prompt = await engine.BuildPromptAsync(request);
    await engine.RecordAssistantResultAsync(prompt.ConversationId, new ProviderResult(
        Id: "result_1",
        Model: request.Model,
        Mode: request.Mode,
        Content: "I created hello.txt.",
        ToolCalls: null,
        Usage: new Usage(prompt.Usage.FinalPromptTokens, 5, prompt.Usage.FinalPromptTokens + 5),
        Context: prompt.Usage));

    var record = await new ConversationStore(dir).LoadAsync("agent-loop");
    AssertEqual(2, record.Messages.Count, "stored message count");
    AssertEqual("assistant", record.Messages[^1].Role, "assistant role");
    AssertEqual("I created hello.txt.", record.Messages[^1].Content, "assistant content");
}

static Task OpenAiStreamingSplitsTextIntoFineGrainedChunks()
{
    var chunks = ApiResponseFactory.SplitTextForStreaming("alpha beta gamma delta", 8).ToList();
    AssertTrue(chunks.Count >= 3, "streaming chunk count");
    AssertTrue(chunks.All(chunk => chunk.Length <= 8), "streaming max chunk length");
    AssertEqual("alpha beta gamma delta", string.Concat(chunks), "streaming round trip");
    return Task.CompletedTask;
}

static async Task OpenAiToolStreamsIncludeStableToolIndexes()
{
    var context = new DefaultHttpContext();
    await using var body = new MemoryStream();
    context.Response.Body = body;
    var session = await ApiResponseFactory.StartOpenAiStreamAsync(context.Response, "deepseek-chat2api-fast");
    var result = new ProviderResult(
        Id: "tool-stream-index",
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Content: string.Empty,
        ToolCalls:
        [
            new ToolCall("call_one", "function", new ToolFunction("first", "{}")),
            new ToolCall("call_two", "function", new ToolFunction("second", "{}"))
        ],
        Usage: new Usage(1, 1, 2),
        Context: new ContextUsage(0, 0, 0, 0, 0, 1, 0));

    await ApiResponseFactory.FinishOpenAiStreamAsync(context.Response, session, result);
    var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

    AssertContains(text, "\"index\":0", "first OpenAI tool stream index");
    AssertContains(text, "\"index\":1", "second OpenAI tool stream index");
}

static async Task ResponsesToolStreamsIncludeOutputItemIndexes()
{
    var context = new DefaultHttpContext();
    await using var body = new MemoryStream();
    context.Response.Body = body;
    var session = await ApiResponseFactory.StartResponsesStreamAsync(context.Response, "deepseek-chat2api-fast");
    var result = new ProviderResult(
        Id: "responses-tool-index",
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Content: string.Empty,
        ToolCalls:
        [
            new ToolCall("call_one", "function", new ToolFunction("first", "{}")),
            new ToolCall("call_two", "function", new ToolFunction("second", "{}"))
        ],
        Usage: new Usage(1, 1, 2),
        Context: new ContextUsage(0, 0, 0, 0, 0, 1, 0));

    await ApiResponseFactory.FinishResponsesStreamAsync(context.Response, session, result);
    var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

    AssertContains(text, "event: response.output_item.added", "Responses tool stream added event");
    AssertContains(text, "\"output_index\":0", "first Responses tool stream output index");
    AssertContains(text, "\"output_index\":1", "second Responses tool stream output index");
    AssertContains(text, "event: response.output_item.done", "Responses tool stream done event");
}

static async Task LegacyStreamHelpersPreserveTextBesideToolCalls()
{
    var result = MakeMixedTextToolResult();

    var openAi = await RenderSseAsync(response => ApiResponseFactory.StreamOpenAiAsync(response, result));
    AssertContains(openAi, "Before the tool call.", "legacy OpenAI mixed text");
    AssertContains(openAi, "\"tool_calls\":", "legacy OpenAI tool call");

    var anthropic = await RenderSseAsync(response => ApiResponseFactory.StreamAnthropicAsync(response, result));
    AssertContains(anthropic, "Before the tool call.", "legacy Anthropic mixed text");
    AssertContains(anthropic, "\"type\":\"tool_use\"", "legacy Anthropic tool use");

    var responses = await RenderSseAsync(response => ApiResponseFactory.StreamResponsesAsync(response, result));
    AssertContains(responses, "Before the tool call.", "legacy Responses mixed text");
    AssertContains(responses, "\"type\":\"function_call\"", "legacy Responses function call");
}

static Task OpenAiJsonPreservesTextBesideToolCalls()
{
    var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ApiResponseFactory.OpenAi(MakeMixedTextToolResult())))!.AsObject();
    var message = payload["choices"]!.AsArray()[0]!["message"]!.AsObject();

    AssertContains(message["content"]!.GetValue<string>(), "Before the tool call.", "OpenAI mixed tool text");
    AssertEqual(1, message["tool_calls"]!.AsArray().Count, "OpenAI mixed tool call count");
    return Task.CompletedTask;
}

static Task AnthropicJsonPreservesTextBesideToolCalls()
{
    var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ApiResponseFactory.Anthropic(MakeMixedTextToolResult())))!.AsObject();
    var content = payload["content"]!.AsArray();

    AssertTrue(content.Any(block => block?["type"]?.GetValue<string>() == "text"), "Anthropic mixed text block");
    AssertTrue(content.Any(block => block?["type"]?.GetValue<string>() == "tool_use"), "Anthropic mixed tool block");
    AssertContains(content[0]!["text"]!.GetValue<string>(), "Before the tool call.", "Anthropic mixed text content");
    return Task.CompletedTask;
}

static Task ResponsesJsonPreservesTextBesideToolCalls()
{
    var payload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ApiResponseFactory.Responses(MakeMixedTextToolResult(), "resp_mixed")))!.AsObject();
    var output = payload["output"]!.AsArray();

    AssertTrue(output.Any(item => item?["type"]?.GetValue<string>() == "message"), "Responses mixed message item");
    AssertTrue(output.Any(item => item?["type"]?.GetValue<string>() == "function_call"), "Responses mixed tool item");
    AssertContains(payload["output_text"]!.GetValue<string>(), "Before the tool call.", "Responses mixed output text");
    return Task.CompletedTask;
}

static async Task ContextEngineAppendsAssistantToolCalls()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("tool-loop", [new ApiMessage("user", "write the file")]);

    var prompt = await engine.BuildPromptAsync(request);
    await engine.RecordAssistantResultAsync(prompt.ConversationId, new ProviderResult(
        Id: "result_2",
        Model: request.Model,
        Mode: request.Mode,
        Content: string.Empty,
        ToolCalls:
        [
            new ToolCall(
                Id: "call_write",
                Type: "function",
                Function: new ToolFunction("write_file", "{\"path\":\"hello.txt\",\"content\":\"hello\"}"))
        ],
        Usage: new Usage(prompt.Usage.FinalPromptTokens, 8, prompt.Usage.FinalPromptTokens + 8),
        Context: prompt.Usage));

    var record = await new ConversationStore(dir).LoadAsync("tool-loop");
    AssertEqual("assistant", record.Messages[^1].Role, "assistant tool role");
    AssertContains(record.Messages[^1].Content, "Assistant tool calls:", "assistant tool marker");
    AssertContains(record.Messages[^1].Content, "write_file", "assistant tool name");
    AssertContains(record.Messages[^1].Content, "call_write", "assistant tool id");
}

static async Task ContextEngineSerializesConcurrentConversationUpdates()
{
    var dir = CreateTempDirectory();
    var gate = new QueuedConversationGate();
    var engine = new ContextEngine(dir, contextIndex: new UnavailableContextIndex(), conversationGate: gate);
    var buildTask = engine.BuildPromptAsync(MakeRequest("concurrent-loop", [
        new ApiMessage("user", "A_FIRST_HISTORY"),
        new ApiMessage("user", "A_SECOND_HISTORY")
    ]));
    await gate.FirstEntered.Task;

    var assistantTask = engine.RecordAssistantResultAsync("concurrent-loop", new ProviderResult(
        Id: "result_concurrent",
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Content: "B_ASSISTANT_RESULT",
        ToolCalls: null,
        Usage: new Usage(1, 1, 2),
        Context: new ContextUsage(0, 0, 0, 0, 0, 1, 0)));
    await gate.SecondQueued.Task;

    gate.ReleaseFirst();
    await Task.WhenAll(buildTask, assistantTask);

    var record = await new ConversationStore(dir).LoadAsync("concurrent-loop");
    AssertContains(string.Join("\n", record.Messages.Select(message => message.Content)), "A_FIRST_HISTORY", "first concurrent history persists");
    AssertContains(string.Join("\n", record.Messages.Select(message => message.Content)), "A_SECOND_HISTORY", "second concurrent history persists");
    AssertContains(string.Join("\n", record.Messages.Select(message => message.Content)), "B_ASSISTANT_RESULT", "concurrent assistant result persists");
}

static async Task ConversationGatesSerializeAcrossEngineInstances()
{
    var directory = CreateTempDirectory();
    var firstGate = new ConversationGate(directory);
    var secondGate = new ConversationGate(directory);
    var firstLease = await firstGate.EnterAsync("shared-conversation", CancellationToken.None);

    var secondLeaseTask = secondGate.EnterAsync("shared-conversation", CancellationToken.None);
    await Task.Yield();
    AssertTrue(!secondLeaseTask.IsCompleted, "second engine waits for shared conversation lease");

    await firstLease.DisposeAsync();
    await using var secondLease = await secondLeaseTask;
}

static Task AnthropicAdapterPreservesToolUseAndResultContent()
{
    var body = JsonNode.Parse("""
    {
      "model": "deepseek-chat2api-expert",
      "messages": [
        {
          "role": "assistant",
          "content": [
            {
              "type": "tool_use",
              "id": "toolu_1",
              "name": "write_file",
              "input": { "path": "hello.txt", "content": "hello" }
            },
            {
              "type": "tool_use",
              "id": "toolu_2",
              "name": "write_file",
              "input": { "path": "second.txt", "content": "second" }
            }
          ]
        },
        {
          "role": "user",
          "content": [
            {
              "type": "tool_result",
              "tool_use_id": "toolu_1",
              "content": "ok"
            }
          ]
        }
      ]
    }
    """)!.AsObject();

    var request = ProtocolAdapters.FromAnthropic(body);

    AssertContains(request.Messages[0].Content, "[tool_use:id=toolu_1;name=write_file]", "first tool_use marker preserves id");
    AssertContains(request.Messages[0].Content, "[tool_use:id=toolu_2;name=write_file]", "second tool_use marker preserves id");
    AssertContains(request.Messages[0].Content, "\"path\":\"hello.txt\"", "tool_use input");
    AssertContains(request.Messages[1].Content, "[tool_result:toolu_1]", "tool_result marker");
    AssertContains(request.Messages[1].Content, "ok", "tool_result content");
    return Task.CompletedTask;
}

static Task ResponsesAdapterPreservesFunctionCallOutputItems()
{
    var body = JsonNode.Parse("""
    {
      "model": "deepseek-chat2api-expert",
      "input": [
        {
          "type": "function_call",
          "call_id": "call_1",
          "name": "Get-Location",
          "arguments": "{}"
        },
        {
          "type": "function_call_output",
          "call_id": "call_1",
          "output": "sandbox/chat2api-codex"
        }
      ]
    }
    """)!.AsObject();

    var request = ProtocolAdapters.FromResponses(body);

    AssertEqual(2, request.Messages.Count, "Responses function item message count");
    AssertContains(request.Messages[0].Content, "Assistant tool calls:", "Responses function call marker");
    AssertContains(request.Messages[0].Content, "Get-Location", "Responses function call name");
    AssertEqual("tool", request.Messages[1].Role, "Responses function output role");
    AssertEqual("call_1", request.Messages[1].ToolCallId, "Responses function output id");
    AssertContains(request.Messages[1].Content, "sandbox/chat2api-codex", "Responses function output content");
    return Task.CompletedTask;
}

static async Task ContextProbeStorePersistsMeasuredLimits()
{
    var dir = CreateTempDirectory();
    var store = new ContextProbeStore(dir);
    await store.SaveAsync(new ContextProbeRecord(
        Mode: "expert",
        AcceptedChars: 12345,
        EstimatedTokens: 3087,
        EffectiveTokens: 2623,
        SafetyRatio: 0.85,
        Source: "test",
        MeasuredAt: DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        Error: null));

    var records = await store.ReadAllAsync();
    var expert = records.Single(record => record.Mode == "expert");
    AssertEqual(12345, expert.AcceptedChars, "accepted chars");
    AssertEqual(3087, expert.EstimatedTokens, "estimated tokens");
    AssertEqual(2623, expert.EffectiveTokens, "effective tokens");
    AssertEqual("test", expert.Source, "source");
}

static async Task ConversationStoreQuarantinesMalformedState()
{
    var dir = CreateTempDirectory();
    var stateDirectory = Path.Combine(dir, "Conversations");
    Directory.CreateDirectory(stateDirectory);
    await File.WriteAllTextAsync(Path.Combine(stateDirectory, "broken.json"), "{");

    var record = await new ConversationStore(dir).LoadAsync("broken");

    AssertEqual("broken", record.Id, "malformed conversation falls back to a new record");
    AssertTrue(!File.Exists(Path.Combine(stateDirectory, "broken.json")), "malformed conversation no longer occupies active path");
    AssertTrue(Directory.EnumerateFiles(stateDirectory, "broken.json.corrupt-*").Any(), "malformed conversation is quarantined");
}

static async Task ConversationStoreIsolatesLegacyFileNameCollisions()
{
    var dir = CreateTempDirectory();
    var store = new ConversationStore(dir);
    var firstId = "a/b";
    var secondId = "a?b";

    await store.SaveAsync(new ConversationRecord
    {
        Id = firstId,
        Messages = [new ApiMessage("user", "FIRST_CONVERSATION_PRIVATE_FACT")]
    });
    await store.SaveAsync(new ConversationRecord
    {
        Id = secondId,
        Messages = [new ApiMessage("user", "SECOND_CONVERSATION_PRIVATE_FACT")]
    });

    var first = await store.LoadAsync(firstId);
    var second = await store.LoadAsync(secondId);

    AssertContains(first.Messages.Single().Content, "FIRST_CONVERSATION_PRIVATE_FACT", "first colliding id retains its own state");
    AssertContains(second.Messages.Single().Content, "SECOND_CONVERSATION_PRIVATE_FACT", "second colliding id retains its own state");
    AssertEqual(2, Directory.EnumerateFiles(Path.Combine(dir, "Conversations"), "*.json").Count(), "colliding ids have separate state files");
}

static async Task ConversationStoreRetriesTransientReplacementSharingViolations()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var dir = CreateTempDirectory();
    var store = new ConversationStore(dir);
    await store.SaveAsync(new ConversationRecord
    {
        Id = "retry-shared",
        Messages = [new ApiMessage("user", "initial")]
    });
    var path = Directory.EnumerateFiles(Path.Combine(dir, "Conversations"), "*.json").Single();
    await using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var replacement = store.SaveAsync(new ConversationRecord
    {
        Id = "retry-shared",
        Messages = [new ApiMessage("user", "replacement")]
    });

    await Task.Delay(250);
    AssertTrue(!replacement.IsCompletedSuccessfully, "replacement waits while target is shared-locked");
    await blocker.DisposeAsync();
    await replacement;

    var reloaded = await store.LoadAsync("retry-shared");
    AssertEqual("replacement", reloaded.Messages.Single().Content, "replacement survives transient sharing violation");
}

static async Task ResponseContinuationStoreQuarantinesMalformedState()
{
    var dir = CreateTempDirectory();
    var stateDirectory = Path.Combine(dir, "ResponseContinuations");
    Directory.CreateDirectory(stateDirectory);
    await File.WriteAllTextAsync(Path.Combine(stateDirectory, "resp_broken.json"), "{");

    var continuation = await new ResponseContinuationStore(dir).ResolveAsync("resp_broken");

    AssertEqual<string?>(null, continuation, "malformed continuation resolves as absent");
    AssertTrue(!File.Exists(Path.Combine(stateDirectory, "resp_broken.json")), "malformed continuation no longer occupies active path");
    AssertTrue(Directory.EnumerateFiles(stateDirectory, "resp_broken.json.corrupt-*").Any(), "malformed continuation is quarantined");
}

static async Task ContextProbeStoreQuarantinesMalformedState()
{
    var dir = CreateTempDirectory();
    var stateDirectory = Path.Combine(dir, "ContextProbes");
    Directory.CreateDirectory(stateDirectory);
    await File.WriteAllTextAsync(Path.Combine(stateDirectory, "fast.json"), "{");

    var records = await new ContextProbeStore(dir).ReadAllAsync();

    AssertEqual(0, records.Count, "malformed probe is excluded from active records");
    AssertTrue(!File.Exists(Path.Combine(stateDirectory, "fast.json")), "malformed probe no longer occupies active path");
    AssertTrue(Directory.EnumerateFiles(stateDirectory, "fast.json.corrupt-*").Any(), "malformed probe is quarantined");
}

static async Task ContextProbeRunnerWritesRealMeasuredLimit()
{
    var dir = CreateTempDirectory();
    var store = new ContextProbeStore(dir);
    var runner = new ContextProbeRunner(store, async request =>
    {
        await Task.Yield();
        return request.Prompt.Length <= 12000
            ? ContextProbeAttempt.Accepted(request.Prompt.Length)
            : ContextProbeAttempt.Rejected("context_length_exceeded");
    });

    var record = await runner.RunAsync(new ContextProbeOptions(
        Mode: "fast",
        MinChars: 1000,
        MaxChars: 20000,
        Thinking: false,
        WebSearch: false,
        SafetyRatio: 0.8),
        CancellationToken.None);

    AssertTrue(record.AcceptedChars <= 12000, "probe accepted upper bound");
    AssertTrue(record.AcceptedChars >= 11000, "probe accepted lower bound");
    AssertEqual((int)(record.EstimatedTokens * 0.8), record.EffectiveTokens, "probe effective tokens");
    AssertEqual<string?>(null, record.Error, "confirmed probe error");
    var saved = (await store.ReadAllAsync()).Single(item => item.Mode == "fast");
    AssertEqual(record.AcceptedChars, saved.AcceptedChars, "probe saved accepted chars");
}

static async Task ContextProbeRunnerRequiresRepeatedExplicitLimitEvidence()
{
    var dir = CreateTempDirectory();
    var runner = new ContextProbeRunner(new ContextProbeStore(dir), request =>
        Task.FromResult(request.Prompt.Length <= 96
            ? ContextProbeAttempt.Accepted(request.Prompt.Length)
            : ContextProbeAttempt.Rejected("context_length_exceeded")));

    var record = await runner.RunAsync(new ContextProbeOptions(
        Mode: "fast",
        MinChars: 64,
        MaxChars: 128,
        Thinking: false,
        WebSearch: false,
        SafetyRatio: 0.8),
        CancellationToken.None);

    AssertTrue(record.EffectiveTokens > 0, "confirmed probe effective tokens");
    AssertEqual<string?>(null, record.Error, "confirmed probe has no error");
}

static async Task ContextProbeRunnerRecordsOperationalFailuresAsUnknown()
{
    var failures = new Dictionary<string, string>
    {
        ["provider_timeout"] = "unknown:timeout",
        ["login_required"] = "unknown:login",
        ["PlaywrightException: selector missing"] = "unknown:dom",
        ["HttpRequestException: connection reset"] = "unknown:network"
    };

    foreach (var (failure, expectedError) in failures)
    {
        var dir = CreateTempDirectory();
        var runner = new ContextProbeRunner(new ContextProbeStore(dir), request =>
            Task.FromResult(request.Prompt.Length <= 64
                ? ContextProbeAttempt.Accepted(request.Prompt.Length)
                : ContextProbeAttempt.Rejected(failure)));

        var record = await runner.RunAsync(new ContextProbeOptions(
            Mode: "fast",
            MinChars: 64,
            MaxChars: 128,
            Thinking: false,
            WebSearch: false,
            SafetyRatio: 0.8),
            CancellationToken.None);

        AssertEqual(0, record.EffectiveTokens, $"{failure} has no effective limit");
        AssertEqual(expectedError, record.Error, $"{failure} is classified as unknown");
    }
}

static async Task ContextProbeRecordsUnboundedUpperTestRange()
{
    var dir = CreateTempDirectory();
    var runner = new ContextProbeRunner(new ContextProbeStore(dir), request =>
        Task.FromResult(ContextProbeAttempt.Accepted(request.Prompt.Length)));

    var record = await runner.RunAsync(new ContextProbeOptions(
        Mode: "expert",
        MinChars: 1024,
        MaxChars: 4096,
        Thinking: false,
        WebSearch: false,
        SafetyRatio: 0.85),
        CancellationToken.None);

    AssertEqual("unknown:upper_bound_not_reached", record.Error, "unbounded probe marker");
}

static async Task EmbeddedHostRunsAndPersistsContextProbes()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "mock"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")));
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var post = await http.PostAsJsonAsync("/admin/probe/context", new
    {
        mode = "expert",
        min_chars = 1000,
        max_chars = 3000,
        safety_ratio = 0.5
    });
    var postText = await post.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, post.StatusCode, "probe post status");
    var created = JsonNode.Parse(postText)!.AsObject();
    AssertEqual("expert", created["mode"]!.GetValue<string>(), "probe mode");
    AssertTrue(created["acceptedChars"]!.GetValue<int>() >= 1000, "probe accepted chars");
    AssertEqual("csharp-host", created["source"]!.GetValue<string>(), "probe source");

    var get = await http.GetAsync("/admin/probe/context");
    var getText = await get.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, get.StatusCode, "probe get status");
    AssertContains(getText, "\"source\":\"csharp-host\"", "probe persisted source");
    AssertContains(getText, "\"mode\":\"expert\"", "probe persisted mode");
}

static async Task ContextEngineAppliesPersistedProbeLimit()
{
    var dir = CreateTempDirectory();
    await new ContextProbeStore(dir).SaveAsync(new ContextProbeRecord(
        Mode: "fast",
        AcceptedChars: 24000,
        EstimatedTokens: 6000,
        EffectiveTokens: 5100,
        SafetyRatio: 0.85,
        Source: "csharp-host",
        MeasuredAt: DateTimeOffset.UtcNow,
        Error: null));
    var engine = new ContextEngine(dir);

    var prompt = await engine.BuildPromptAsync(MakeRequest("probed-budget", [new ApiMessage("user", "use the measured budget")]));

    AssertEqual(5100, prompt.Usage.BudgetTokens, "persisted probe context budget");
}

static async Task ContextEngineIgnoresInconclusiveProbeLimits()
{
    var dir = CreateTempDirectory();
    await new ContextProbeStore(dir).SaveAsync(new ContextProbeRecord(
        Mode: "fast",
        AcceptedChars: 4096,
        EstimatedTokens: 1024,
        EffectiveTokens: 870,
        SafetyRatio: 0.85,
        Source: "csharp-host",
        MeasuredAt: DateTimeOffset.UtcNow,
        Error: "upper_bound_not_reached"));
    var engine = new ContextEngine(dir);

    var prompt = await engine.BuildPromptAsync(MakeRequest("unbounded-probe-budget", [new ApiMessage("user", "use the default budget") ]));

    AssertEqual(28800, prompt.Usage.BudgetTokens, "default budget for inconclusive probe");
}

static async Task ContextEngineContinuesStandardClientSessionsWithoutPrivateId()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var firstRequest = new ProviderRequest(
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Messages: [new ApiMessage("user", "remember the deployment target")],
        Tools: null,
        ToolChoice: null,
        Thinking: false,
        WebSearch: false,
        ConversationId: null,
        MaxTokens: null,
        Temperature: null);

    var firstPrompt = await engine.BuildPromptAsync(firstRequest);
    await engine.RecordAssistantResultAsync(firstPrompt.ConversationId, new ProviderResult(
        Id: "result_1",
        Model: firstRequest.Model,
        Mode: firstRequest.Mode,
        Content: "The deployment target is staging.",
        ToolCalls: null,
        Usage: new Usage(0, 0, 0),
        Context: new ContextUsage(0, 0, 0, 0, 0, 0, 0)));

    var continuation = firstRequest with
    {
        Messages = [
            new ApiMessage("user", "remember the deployment target"),
            new ApiMessage("assistant", "The deployment target is staging."),
            new ApiMessage("user", "which target did I mention?")
        ]
    };
    var continuedPrompt = await engine.BuildPromptAsync(continuation);

    AssertEqual(firstPrompt.ConversationId, continuedPrompt.ConversationId, "standard client continuation id");
    AssertContains(continuedPrompt.Prompt, "deployment target is staging", "continued prompt retains prior assistant result");
}

static async Task SqliteVectorIndexRetrievesRelevantHistory()
{
    var dir = CreateTempDirectory();
    var index = new SqliteVectorContextIndex(Path.Combine(dir, "context-vectors.db"));
    await index.UpsertAsync("conv", [
        new ApiMessage("user", "bread fermentation and sourdough hydration"),
        new ApiMessage("assistant", "CUDA kernels, GPU occupancy, tensor cores and shared memory tuning")
    ], CancellationToken.None);

    await using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "context-vectors.db")}"))
    {
        await connection.OpenAsync();
        SqliteVectorContextIndex.LoadVectorExtension(connection);
        await using var vectorTable = connection.CreateCommand();
        vectorTable.CommandText = "select count(*) from context_vectors";
        var vectorRows = Convert.ToInt32(await vectorTable.ExecuteScalarAsync());
        AssertEqual(2, vectorRows, "sqlite-vec row count");
    }

    var matches = await index.SearchAsync("conv", "gpu cuda occupancy", 2, 4000, CancellationToken.None);

    AssertTrue(File.Exists(Path.Combine(dir, "context-vectors.db")), "sqlite vector database exists");
    AssertEqual(1, matches.Count, "vector match count");
    AssertContains(matches[0].Content, "CUDA kernels", "vector relevant result");
}

static async Task SqliteVectorIndexChunksOversizedHistoryWithinRetrievalBudget()
{
    var dir = CreateTempDirectory();
    var index = new SqliteVectorContextIndex(Path.Combine(dir, "context-vectors.db"));
    var oversizedHistory = string.Join(' ', Enumerable.Repeat("filler", 2_000)) + " rare archive gpu occupancy fact";

    await index.UpsertAsync("conv", [
        new ApiMessage("assistant", oversizedHistory)
    ], CancellationToken.None);

    var matches = await index.SearchAsync("conv", "rare archive gpu occupancy", 3, 1024, CancellationToken.None);

    AssertTrue(matches.Count > 0, "oversized history produces a retrieval result");
    AssertTrue(matches.Sum(message => TokenEstimator.Estimate(message.Content)) <= 1024, "retrieval result stays within token budget");
    AssertTrue(matches.Any(message => message.Content.Contains("rare archive gpu occupancy fact", StringComparison.Ordinal)), "retrieval retains the relevant oversized-history chunk");
}

static async Task ContextEngineFallsBackWhenVectorIndexIsUnavailable()
{
    var dir = CreateTempDirectory();
    var index = new UnavailableContextIndex();
    var engine = new ContextEngine(dir, contextIndex: index);
    var request = MakeRequest("index-fallback", [
        new ApiMessage("user", "remember the staging deployment target"),
        new ApiMessage("assistant", "The target is staging."),
        new ApiMessage("user", "which deployment target did I mention?")
    ]);

    var first = await engine.BuildPromptAsync(request);
    var second = await engine.BuildPromptAsync(request);

    AssertEqual("context_index_unavailable", first.Usage.Diagnostic, "first fallback diagnostic");
    AssertEqual("context_index_unavailable", second.Usage.Diagnostic, "stable fallback diagnostic");
    AssertEqual(1, index.UpsertCalls, "unavailable index is disabled after its first failure");
    AssertContains(first.Prompt, "staging deployment target", "lexical fallback prompt content");
}

static async Task EmbeddedHostStreamsProviderDeltas()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "mock"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")));
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "stream this response" } }
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "stream status");
    AssertContains(body, "data: [DONE]", "OpenAI stream terminator");
    AssertTrue(body.Split("\"content\":", StringSplitOptions.None).Length > 3, "multiple provider text delta events");
}

static async Task EmbeddedHostStreamsOtherProtocols()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "mock"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")));
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var anthropic = await http.PostAsJsonAsync("/v1/messages", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        max_tokens = 128,
        messages = new[] { new { role = "user", content = "stream Anthropic" } }
    });
    var anthropicBody = await anthropic.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, anthropic.StatusCode, "Anthropic stream status");
    AssertContains(anthropicBody, "event: content_block_delta", "Anthropic text delta event");
    AssertContains(anthropicBody, "event: message_stop", "Anthropic completion event");

    var responses = await http.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        input = "stream Responses"
    });
    var responsesBody = await responses.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, responses.StatusCode, "Responses stream status");
    AssertContains(responsesBody, "event: response.output_item.added", "Responses output item start event");
    AssertContains(responsesBody, "event: response.content_part.added", "Responses content part start event");
    AssertContains(responsesBody, "event: response.output_text.delta", "Responses text delta event");
    AssertContains(responsesBody, "\"item_id\":", "Responses delta item association");
    AssertContains(responsesBody, "\"output_index\":0", "Responses delta output index");
    AssertContains(responsesBody, "\"content_index\":0", "Responses delta content index");
    AssertTrue(
        responsesBody.IndexOf("event: response.output_item.added", StringComparison.Ordinal) < responsesBody.IndexOf("event: response.output_text.delta", StringComparison.Ordinal),
        "Responses output item starts before text delta");
    AssertContains(responsesBody, "event: response.completed", "Responses completion event");
}

static async Task EmbeddedHostStreamsTextBeforeBufferedToolEvents()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter { Response = MixedTextToolEnvelope };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var openAi = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "call lookup" } },
        tools = new[] { new { type = "function", function = new { name = "lookup", parameters = new { type = "object" } } } }
    });
    var openAiBody = await openAi.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, openAi.StatusCode, "mixed tool OpenAI stream status");
    AssertContains(openAiBody, "Before the tool call.", "mixed tool OpenAI text delta");
    AssertTrue(openAiBody.IndexOf("Before the tool call.", StringComparison.Ordinal) < openAiBody.IndexOf("\"tool_calls\":", StringComparison.Ordinal), "mixed tool OpenAI text precedes tool call");
    AssertContains(openAiBody, "data: [DONE]", "mixed tool OpenAI stream terminator");

    var anthropic = await http.PostAsJsonAsync("/v1/messages", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        max_tokens = 128,
        messages = new[] { new { role = "user", content = "call lookup" } },
        tools = new[] { new { name = "lookup", input_schema = new { type = "object" } } }
    });
    var anthropicBody = await anthropic.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, anthropic.StatusCode, "mixed tool Anthropic stream status");
    AssertContains(anthropicBody, "Before the tool call.", "mixed tool Anthropic text delta");
    AssertTrue(anthropicBody.IndexOf("Before the tool call.", StringComparison.Ordinal) < anthropicBody.IndexOf("\"type\":\"tool_use\"", StringComparison.Ordinal), "mixed tool Anthropic text precedes tool use");
    AssertContains(anthropicBody, "\"index\":1", "mixed tool Anthropic tool index follows text");
    AssertContains(anthropicBody, "event: message_stop", "mixed tool Anthropic stream terminator");

    var responses = await http.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        input = "call lookup",
        tools = new[] { new { type = "function", name = "lookup", parameters = new { type = "object" } } }
    });
    var responsesBody = await responses.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, responses.StatusCode, "mixed tool Responses stream status");
    AssertContains(responsesBody, "Before the tool call.", "mixed tool Responses text delta");
    AssertTrue(responsesBody.IndexOf("Before the tool call.", StringComparison.Ordinal) < responsesBody.IndexOf("\"type\":\"function_call\"", StringComparison.Ordinal), "mixed tool Responses text precedes function call");
    AssertContains(responsesBody, "\"output_index\":1", "mixed tool Responses function index follows text");
    AssertContains(responsesBody, "event: response.completed", "mixed tool Responses stream terminator");
}

static async Task EmbeddedHostTerminatesFailedProviderStreams()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter { SendError = new TimeoutException("synthetic provider timeout") };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var openAi = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "trigger OpenAI stream failure" } }
    });
    var openAiBody = await openAi.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, openAi.StatusCode, "failed OpenAI stream status");
    AssertContains(openAiBody, "\"code\":\"provider_timeout\"", "failed OpenAI stream error code");
    AssertTrue(!openAiBody.Contains("data: [DONE]", StringComparison.Ordinal), "failed OpenAI stream omits success terminator");

    var anthropic = await http.PostAsJsonAsync("/v1/messages", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        max_tokens = 128,
        messages = new[] { new { role = "user", content = "trigger Anthropic stream failure" } }
    });
    var anthropicBody = await anthropic.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, anthropic.StatusCode, "failed Anthropic stream status");
    AssertContains(anthropicBody, "event: error", "failed Anthropic stream error event");
    AssertContains(anthropicBody, "\"type\":\"api_error\"", "failed Anthropic stream error type");
    AssertTrue(!anthropicBody.Contains("event: message_stop", StringComparison.Ordinal), "failed Anthropic stream omits success terminator");

    var responses = await http.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        input = "trigger Responses stream failure"
    });
    var responsesBody = await responses.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, responses.StatusCode, "failed Responses stream status");
    AssertContains(responsesBody, "event: response.failed", "failed Responses stream error event");
    AssertContains(responsesBody, "\"status\":\"failed\"", "failed Responses stream status payload");
    AssertContains(responsesBody, "\"code\":\"provider_timeout\"", "failed Responses stream error code");
    AssertTrue(!responsesBody.Contains("event: response.completed", StringComparison.Ordinal), "failed Responses stream omits success terminator");
}

static async Task ResponsesPreviousResponseIdSurvivesHostRestart()
{
    var dir = CreateTempDirectory();
    var firstPort = GetFreePort();
    var firstAdapter = new FakeWebAdapter { Response = "FIRST-RESPONSE" };
    string responseId;
    await using (var firstServer = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = firstPort, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => firstAdapter))
    {
        await firstServer.StartAsync();
        using var firstHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{firstPort}") };
        var firstResponse = await firstHttp.PostAsJsonAsync("/v1/responses", new
        {
            model = "deepseek-chat2api-expert",
            input = "remember the first response"
        });
        var firstBody = JsonNode.Parse(await firstResponse.Content.ReadAsStringAsync())!.AsObject();
        AssertEqual(HttpStatusCode.OK, firstResponse.StatusCode, "first Responses status");
        responseId = firstBody["id"]!.GetValue<string>();
    }

    var secondPort = GetFreePort();
    var secondAdapter = new FakeWebAdapter { Response = "SECOND-RESPONSE" };
    await using var secondServer = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = secondPort, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => secondAdapter);
    await secondServer.StartAsync();
    using var secondHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{secondPort}") };

    var continuation = await secondHttp.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        previous_response_id = responseId,
        input = "continue after restart"
    });
    AssertEqual(HttpStatusCode.OK, continuation.StatusCode, "continued Responses status");

    var missing = await secondHttp.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        previous_response_id = "resp_missing",
        input = "continue missing response"
    });
    var missingBody = await missing.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.NotFound, missing.StatusCode, "missing previous response status");
    AssertContains(missingBody, "\"code\":\"previous_response_not_found\"", "missing previous response code");
}

static async Task BrowserProviderRequiresExplicitLogin()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter
    {
        SendError = new ApiRequestException(401, "login_required", "DeepSeek session is not logged in.")
    };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages = new[] { new { role = "user", content = "requires manual login" } }
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode, "login required status");
    AssertContains(body, "login_required", "login required error code");
}

static async Task OfflineModeRefusesBrowserProvider()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter();
    var factoryCalls = 0;
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser",
            OfflineMode = true
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) =>
        {
            factoryCalls += 1;
            return adapter;
        });
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var health = await http.GetAsync("/health");
    AssertEqual(HttpStatusCode.OK, health.StatusCode, "offline health status");

    var completion = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages = new[] { new { role = "user", content = "offline completion" } }
    });
    await AssertProviderOfflineAsync(completion, "offline completion");

    var stream = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "offline stream" } }
    });
    await AssertProviderOfflineAsync(stream, "offline stream");
    AssertTrue(!(await stream.Content.ReadAsStringAsync()).Contains("data:", StringComparison.Ordinal), "offline stream has no SSE event");

    await AssertProviderOfflineAsync(await http.GetAsync("/auth/login"), "offline login");
    await AssertProviderOfflineAsync(await http.PostAsJsonAsync("/auth/wait", new { timeout_ms = 1 }), "offline wait");
    await AssertProviderOfflineAsync(await http.PostAsJsonAsync("/admin/probe/context", new { mode = "expert" }), "offline probe");

    AssertEqual(0, factoryCalls, "offline mode browser adapter factory calls");
    AssertEqual(0, adapter.CallCount, "offline mode browser adapter calls");
}

static async Task OfflineModeRefusesNonMockProviderAliases()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter();
    var factoryCalls = 0;
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser ",
            OfflineMode = true
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) =>
        {
            factoryCalls += 1;
            return adapter;
        });
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages = new[] { new { role = "user", content = "offline alias" } }
    });

    await AssertProviderOfflineAsync(response, "offline alias completion");
    AssertEqual(0, factoryCalls, "offline alias browser adapter factory calls");
    AssertEqual(0, adapter.CallCount, "offline alias browser adapter calls");
}

static async Task EmbeddedHostRejectsNonLoopbackBinding()
{
    var dir = CreateTempDirectory();
    var factoryCalls = 0;
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "0.0.0.0",
            Port = GetFreePort(),
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) =>
        {
            factoryCalls += 1;
            return new FakeWebAdapter();
        });

    try
    {
        await server.StartAsync();
        throw new InvalidOperationException("non-loopback binding was accepted");
    }
    catch (InvalidOperationException exception)
    {
        AssertContains(exception.Message, "loopback", "non-loopback binding rejection");
    }

    AssertEqual(0, factoryCalls, "non-loopback binding must reject before adapter construction");
}

static async Task EmbeddedHostReturnsInvalidToolEnvelopeError()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter
    {
        Response = "<chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":"
    };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages = new[] { new { role = "user", content = "call lookup" } },
        tools = new[]
        {
            new
            {
                type = "function",
                function = new { name = "lookup", parameters = new { type = "object" } }
            }
        }
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.BadGateway, response.StatusCode, "invalid tool envelope HTTP status");
    AssertContains(body, "\"code\":\"invalid_tool_envelope\"", "invalid tool envelope API code");
    AssertEqual(3, adapter.Prompts.Count, "invalid tool envelope provider attempts");
}

static async Task EmbeddedHostRejectsInvalidToolStreamsBeforeSendingSse()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter
    {
        Response = "<chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":"
    };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var openAi = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "call lookup" } },
        tools = new[] { new { type = "function", function = new { name = "lookup" } } }
    });
    var openAiBody = await openAi.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.BadGateway, openAi.StatusCode, "OpenAI invalid tool stream status");
    AssertContains(openAiBody, "\"code\":\"invalid_tool_envelope\"", "OpenAI invalid tool stream error");
    AssertTrue(!openAiBody.Contains("data:", StringComparison.Ordinal), "OpenAI invalid tool stream has no SSE event");

    var anthropic = await http.PostAsJsonAsync("/v1/messages", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        messages = new[] { new { role = "user", content = "call lookup" } },
        tools = new[] { new { name = "lookup", input_schema = new { type = "object" } } }
    });
    var anthropicBody = await anthropic.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.BadGateway, anthropic.StatusCode, "Anthropic invalid tool stream status");
    AssertContains(anthropicBody, "\"code\":\"invalid_tool_envelope\"", "Anthropic invalid tool stream error");
    AssertTrue(!anthropicBody.Contains("event:", StringComparison.Ordinal), "Anthropic invalid tool stream has no SSE event");

    var responses = await http.PostAsJsonAsync("/v1/responses", new
    {
        model = "deepseek-chat2api-expert",
        stream = true,
        input = "call lookup",
        tools = new[] { new { type = "function", name = "lookup", parameters = new { type = "object" } } }
    });
    var responsesBody = await responses.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.BadGateway, responses.StatusCode, "Responses invalid tool stream status");
    AssertContains(responsesBody, "\"code\":\"invalid_tool_envelope\"", "Responses invalid tool stream error");
    AssertTrue(!responsesBody.Contains("event:", StringComparison.Ordinal), "Responses invalid tool stream has no SSE event");
}

static async Task BrowserProviderTimeoutReturnsGatewayTimeout()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter { SendError = new TimeoutException("upstream response timed out") };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages = new[] { new { role = "user", content = "trigger timeout" } }
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.GatewayTimeout, response.StatusCode, "provider timeout status");
    AssertContains(body, "provider_timeout", "provider timeout error code");
}

static async Task EmbeddedHostRejectsMalformedJson()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "mock" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")));
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

    var response = await http.PostAsync("/v1/chat/completions", content);
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, "malformed JSON status");
    AssertContains(body, "invalid_request", "malformed JSON error code");
}

static async Task EmbeddedHostRejectsExternalFilePath()
{
    var dir = CreateTempDirectory();
    var externalPath = Path.Combine(Path.GetTempPath(), $"chat2api-external-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(externalPath, "private local content");
    try
    {
        var port = GetFreePort();
        await using var server = new EmbeddedChat2ApiServer(
            new TraySettings
            {
                Host = "127.0.0.1",
                Port = port,
                Provider = "mock"
            },
            dir,
            new FileLogger(Path.Combine(dir, "Logs")));
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var response = await http.PostAsJsonAsync("/v1/files", new { path = externalPath, mime_type = "text/plain" });
        var body = await response.Content.ReadAsStringAsync();

        AssertEqual(HttpStatusCode.BadRequest, response.StatusCode, "external file path status");
        AssertContains(body, "file_path_not_allowed", "external file path error code");
        AssertTrue(!Directory.EnumerateFiles(Path.Combine(dir, "Uploads")).Any(), "external file was not copied");
    }
    finally
    {
        File.Delete(externalPath);
    }
}

static async Task EmbeddedHostReturnsUploadedFileMetadata()
{
    var dir = CreateTempDirectory();
    var sourcePath = Path.Combine(dir, "source.txt");
    await File.WriteAllTextAsync(sourcePath, "chat2api file metadata");
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "mock"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")));
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var upload = await http.PostAsJsonAsync("/v1/files", new { path = sourcePath, mime_type = "text/plain" });
    var uploadBody = await upload.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.OK, upload.StatusCode, "file upload status");
    var created = JsonNode.Parse(uploadBody)?.AsObject() ?? throw new InvalidOperationException($"upload response was empty: {uploadBody}");
    var fileId = created["id"]?.GetValue<string>() ?? throw new InvalidOperationException("upload response had no id");

    var metadata = await http.GetAsync($"/v1/files/{fileId}");
    var body = await metadata.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, metadata.StatusCode, "file metadata status");
    AssertContains(body, fileId, "file metadata id");
    AssertContains(body, "source.txt", "file metadata filename");
}

static async Task EmbeddedHostForwardsUploadedFilesToBrowserAdapter()
{
    var dir = CreateTempDirectory();
    var sourcePath = Path.Combine(dir, "vision.txt");
    await File.WriteAllTextAsync(sourcePath, "vision fixture");
    var adapter = new FakeWebAdapter();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var upload = await http.PostAsJsonAsync("/v1/files", new { path = sourcePath, mime_type = "text/plain" });
    var created = JsonNode.Parse(await upload.Content.ReadAsStringAsync())!.AsObject();
    var fileId = created["id"]!.GetValue<string>();
    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-vision",
        messages = new[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "inspect the file" },
                    new { type = "input_file", file_id = fileId }
                }
            }
        }
    });

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "vision file forwarding status");
    AssertTrue(adapter.UploadedFiles.Any(files => files.Any(file => file.Id == fileId && file.Filename == "vision.txt")), "browser adapter received uploaded file");
}

static async Task EmbeddedHostPromotesUploadedImageFilesToVision()
{
    var dir = CreateTempDirectory();
    var sourcePath = Path.Combine(dir, "source.png");
    await File.WriteAllBytesAsync(sourcePath, [137, 80, 78, 71]);
    var adapter = new FakeWebAdapter();
    var port = GetFreePort();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings { Host = "127.0.0.1", Port = port, Provider = "browser" },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var upload = await http.PostAsJsonAsync("/v1/files", new { path = sourcePath, mime_type = "image/png" });
    var fileId = JsonNode.Parse(await upload.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        messages = new[] { new { role = "user", content = "inspect the uploaded image" } },
        file_ids = new[] { fileId }
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "uploaded image completion status");
    AssertContains(body, "deepseek-chat2api-vision", "uploaded image response model");
}

static async Task ContextEngineUsesModelIncrementalSummarizer()
{
    var dir = CreateTempDirectory();
    var calls = 0;
    var engine = new ContextEngine(dir, new DelegateIncrementalSummarizer(async (request, _) =>
    {
        await Task.Yield();
        calls += 1;
        return $"MODEL-SUMMARY:{request.NewMessages.Count}:{request.OldSummary.Length}";
    }));
    var messages = Enumerable.Range(0, 140)
        .Select(index => new ApiMessage(index % 2 == 0 ? "user" : "assistant", $"message-{index} " + new string('x', 1200)))
        .Append(new ApiMessage("user", "final request about message 1"))
        .ToList();

    await engine.BuildPromptAsync(MakeRequest("summary-loop", messages));

    var record = await new ConversationStore(dir).LoadAsync("summary-loop");
    AssertTrue(calls > 0, "model summarizer calls");
    AssertContains(record.Summary, "MODEL-SUMMARY", "stored model summary");
}

static async Task DelegateIncrementalSummarizerForwardsCancellation()
{
    var observedToken = default(CancellationToken);
    var summarizer = new DelegateIncrementalSummarizer((_, cancellationToken) =>
    {
        observedToken = cancellationToken;
        return Task.FromResult("summary");
    });
    using var cancellation = new CancellationTokenSource();
    var request = new IncrementalSummaryRequest("model", "fast", string.Empty, [], 1024);

    await summarizer.SummarizeAsync(request, cancellation.Token);

    AssertEqual(cancellation.Token, observedToken, "summary delegate cancellation token");
}

static async Task ContextEngineFallsBackWhenModelIncrementalSummarizerFails()
{
    var dir = CreateTempDirectory();
    var calls = 0;
    var engine = new ContextEngine(dir, new DelegateIncrementalSummarizer((request, _) =>
    {
        calls += 1;
        throw new InvalidOperationException("synthetic summary provider failure");
    }));
    var messages = Enumerable.Range(0, 140)
        .Select(index => new ApiMessage(index % 2 == 0 ? "user" : "assistant", $"message-{index} " + new string('x', 1200)))
        .Append(new ApiMessage("user", "final request after the summary failure"))
        .ToList();

    var prompt = await engine.BuildPromptAsync(MakeRequest("summary-fallback", messages));
    var record = await new ConversationStore(dir).LoadAsync("summary-fallback");

    AssertTrue(calls > 0, "failing model summarizer call count");
    AssertEqual("summary_model_unavailable", prompt.Usage.Diagnostic, "summary fallback diagnostic");
    AssertTrue(prompt.Usage.SummaryTokens > 0, "summary fallback token count");
    AssertTrue(!string.IsNullOrWhiteSpace(record.Summary), "stored extractive fallback summary");
}

static async Task ContextEngineSummarizesOnlyNewlyEvictedMessages()
{
    var dir = CreateTempDirectory();
    var calls = new List<IncrementalSummaryRequest>();
    var engine = new ContextEngine(dir, new DelegateIncrementalSummarizer(async (request, _) =>
    {
        await Task.Yield();
        calls.Add(request);
        return $"summary-{calls.Count}";
    }));
    var messages = Enumerable.Range(0, 150)
        .Select(index => new ApiMessage(index % 2 == 0 ? "user" : "assistant", $"message-{index} " + new string('x', 1200)))
        .ToList();

    await engine.BuildPromptAsync(MakeRequest("incremental-summary-loop", messages));
    var firstCallCount = calls.Count;
    var firstNewMessageCount = calls.Sum(call => call.NewMessages.Count);
    messages.Add(new ApiMessage("user", "one more request " + new string('y', 1200)));
    await engine.BuildPromptAsync(MakeRequest("incremental-summary-loop", messages));

    var laterCalls = calls.Skip(firstCallCount).ToList();
    AssertTrue(laterCalls.Count > 0, "second summary has model invocations");
    AssertTrue(laterCalls.Sum(call => call.NewMessages.Count) > 0, "second summary has newly evicted messages");
    AssertTrue(laterCalls.Sum(call => call.NewMessages.Count) < firstNewMessageCount, "second summary excludes already summarized messages");
}

static async Task ContextEngineSummarizesManagedOverflowBeforeTrimming()
{
    var dir = CreateTempDirectory();
    var summaries = new List<IncrementalSummaryRequest>();
    var engine = new ContextEngine(
        dir,
        new DelegateIncrementalSummarizer((request, _) =>
        {
            summaries.Add(request);
            return Task.FromResult("OVERFLOW_SUMMARY");
        }),
        new UnavailableContextIndex());
    var oldestFact = "CRITICAL_OVERFLOW_FACT preserve this before trimming " + new string('x', 4_000_100);
    var request = MakeRequest("managed-overflow", [
        new ApiMessage("user", oldestFact),
        new ApiMessage("user", "latest request")
    ]);

    var prompt = await engine.BuildPromptAsync(request);
    var record = await new ConversationStore(dir).LoadAsync("managed-overflow");

    AssertTrue(summaries.Count > 1, "managed overflow summary call count");
    AssertContains(summaries[0].NewMessages[0].Content, "CRITICAL_OVERFLOW_FACT", "managed overflow reaches summarizer");
    AssertContains(prompt.Prompt, "OVERFLOW_SUMMARY", "managed overflow summary reaches prompt");
    AssertEqual("OVERFLOW_SUMMARY", record.Summary, "managed overflow summary persists");
}

static async Task ContextEngineIndexesManagedOverflowForSemanticRetrieval()
{
    var dir = CreateTempDirectory();
    var index = new CapturingContextIndex();
    var engine = new ContextEngine(
        dir,
        new DelegateIncrementalSummarizer((_, _) => Task.FromResult("OVERFLOW_SUMMARY")),
        index);
    var archivedFact = "ARCHIVED_GPU_FACT tensor occupancy settings " + new string('x', 4_000_100);

    await engine.BuildPromptAsync(MakeRequest("managed-overflow-index", [
        new ApiMessage("user", archivedFact),
        new ApiMessage("user", "latest request")
    ]));

    AssertTrue(index.Indexed.Any(message => message.Content.Contains("ARCHIVED_GPU_FACT", StringComparison.Ordinal)), "managed overflow reaches vector index");

    var followUp = await engine.BuildPromptAsync(MakeRequest("managed-overflow-index", [
        new ApiMessage("user", "recall ARCHIVED_GPU_FACT tensor occupancy")
    ]));

    AssertContains(followUp.Prompt, "ARCHIVED_GPU_FACT", "retrieval restores managed overflow fact");
}

static async Task ContextEngineRejectsOversizedCurrentMessageBeforeProviderWork()
{
    var summarizerCalls = 0;
    var engine = new ContextEngine(
        CreateTempDirectory(),
        new DelegateIncrementalSummarizer((_, _) =>
        {
            summarizerCalls += 1;
            return Task.FromResult("unexpected summary");
        }),
        new CapturingContextIndex());
    var oversizedCurrentMessage = "CURRENT_MESSAGE_OVER_BUDGET " + new string('x', 4_000_100);

    try
    {
        await engine.BuildPromptAsync(MakeRequest("oversized-current", [
            new ApiMessage("user", oversizedCurrentMessage)
        ]));
        throw new InvalidOperationException("oversized current message was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(400, error.StatusCode, "oversized current message status");
        AssertEqual("context_length_exceeded", error.Code, "oversized current message code");
    }

    AssertEqual(0, summarizerCalls, "oversized current message does not invoke summarizer");
}

static async Task ContextEngineChunksOversizedSummaryInputs()
{
    var dir = CreateTempDirectory();
    var calls = new List<IncrementalSummaryRequest>();
    var engine = new ContextEngine(
        dir,
        new DelegateIncrementalSummarizer((request, _) =>
        {
            calls.Add(request);
            return Task.FromResult($"SUMMARY_CHUNK_{calls.Count}");
        }),
        new UnavailableContextIndex());
    var oversizedHistory = "CRITICAL_CHUNKED_HISTORY " + new string('x', 4_000_100);

    await engine.BuildPromptAsync(MakeRequest("chunked-summary", [
        new ApiMessage("user", oversizedHistory),
        new ApiMessage("user", "latest request")
    ]));

    AssertTrue(calls.Count > 1, "oversized history is summarized in multiple model calls");
    AssertTrue(calls.All(call => call.NewMessages.Sum(message => TokenEstimator.Estimate(message.Content)) <= 16_000), "every summary model input stays within the chunk budget");
    AssertContains(calls[0].NewMessages[0].Content, "CRITICAL_CHUNKED_HISTORY", "first chunk preserves the oldest critical fact");
    AssertTrue(calls.Skip(1).All(call => call.OldSummary.StartsWith("SUMMARY_CHUNK_", StringComparison.Ordinal)), "each later chunk receives the previous rolling summary");
}

static async Task EmbeddedHostDelegatesIncrementalSummariesToWebAdapter()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    var messages = Enumerable.Range(0, 150)
        .Select(index => new { role = index % 2 == 0 ? "user" : "assistant", content = $"message-{index} " + new string('x', 1200) })
        .ToArray();

    var response = await http.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "deepseek-chat2api-expert",
        messages,
        chat2api = new { conversation_id = "host-summary" }
    });

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "host summary response status");
    AssertTrue(adapter.Prompts.Any(prompt => prompt.StartsWith("Update the rolling summary", StringComparison.Ordinal)), "host sent summary prompt to adapter");
    var record = await new ConversationStore(dir).LoadAsync("host-summary");
    AssertEqual("MODEL-HOST-SUMMARY", record.Summary, "host stored adapter summary");
}

static async Task BrowserHostProbeDelegatesToWebAdapter()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/admin/probe/context", new
    {
        mode = "expert",
        min_chars = 1000,
        max_chars = 3000,
        safety_ratio = 0.8
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "browser probe response status");
    AssertContains(body, "\"source\":\"csharp-host\"", "browser probe source");
    AssertTrue(adapter.Prompts.Any(prompt => prompt.StartsWith("chat2api context probe", StringComparison.Ordinal)), "browser adapter received context probe prompt");
    var record = (await new ContextProbeStore(dir).ReadAllAsync()).Single(item => item.Mode == "expert");
    AssertEqual("csharp-host", record.Source, "persisted browser probe source");
}

static async Task BrowserContextProbeBoundsProviderTimeout()
{
    var dir = CreateTempDirectory();
    var port = GetFreePort();
    var adapter = new FakeWebAdapter { SendDelay = TimeSpan.FromSeconds(5) };
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = port,
            Provider = "browser",
            ContextProbeTimeoutSeconds = 1
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

    var response = await http.PostAsJsonAsync("/admin/probe/context", new
    {
        mode = "expert",
        min_chars = 64,
        max_chars = 64,
        safety_ratio = 0.8
    });
    var body = await response.Content.ReadAsStringAsync();

    AssertEqual(HttpStatusCode.OK, response.StatusCode, "bounded probe status");
    AssertContains(body, "unknown:timeout", "bounded probe error");
    var record = (await new ContextProbeStore(dir).ReadAllAsync()).Single(item => item.Mode == "expert");
    AssertEqual("unknown:timeout", record.Error, "persisted timeout error");
}

static async Task EmbeddedHostStartsLoginThroughAdapter()
{
    var dir = CreateTempDirectory();
    var adapter = new FakeWebAdapter();
    await using var server = new EmbeddedChat2ApiServer(
        new TraySettings
        {
            Host = "127.0.0.1",
            Port = GetFreePort(),
            Provider = "browser"
        },
        dir,
        new FileLogger(Path.Combine(dir, "Logs")),
        (_, _, _) => adapter);
    await server.StartAsync();

    var status = await server.BeginLoginAsync(CancellationToken.None);

    AssertTrue(status.LoggedIn, "direct login status");
    AssertEqual(1, adapter.BeginLoginCalls, "direct adapter login call count");
}

static Task ProtocolAdaptersPreserveVisionFileIds()
{
    var body = JsonNode.Parse("""
        {
          "model": "deepseek-chat2api-vision",
          "messages": [{
            "role": "user",
            "content": [
              { "type": "text", "text": "inspect this image" },
              { "type": "input_image", "file_id": "file_image123" }
            ]
          }]
        }
        """)!.AsObject();

    var request = ProtocolAdapters.FromOpenAi(body);

    AssertEqual("vision", request.Mode, "vision mode from image content");
    AssertEqual("file_image123", request.FileIds!.Single(), "vision file id");
    return Task.CompletedTask;
}

static Task ProtocolPolicyDefaultsToExpertAndRejectsFast()
{
    var defaultOpenAi = ProtocolAdapters.FromOpenAi(JsonNode.Parse("""
        { "messages": [{ "role": "user", "content": "hello" }] }
        """)!.AsObject());
    AssertEqual("expert", defaultOpenAi.Mode, "OpenAI default mode");

    var defaultAnthropic = ProtocolAdapters.FromAnthropic(JsonNode.Parse("""
        { "messages": [{ "role": "user", "content": "hello" }] }
        """)!.AsObject());
    AssertEqual("expert", defaultAnthropic.Mode, "Anthropic default mode");

    var defaultResponses = ProtocolAdapters.FromResponses(JsonNode.Parse("""
        { "input": "hello" }
        """)!.AsObject());
    AssertEqual("expert", defaultResponses.Mode, "Responses default mode");

    foreach (var body in new[]
    {
        JsonNode.Parse("""{ "model": "deepseek-chat2api-fast", "messages": [{ "role": "user", "content": "hello" }] }""")!.AsObject(),
        JsonNode.Parse("""{ "chat2api": { "mode": "fast" }, "messages": [{ "role": "user", "content": "hello" }] }""")!.AsObject()
    })
    {
        try
        {
            ProtocolAdapters.FromOpenAi(body);
            throw new InvalidOperationException("expected fast mode to be rejected");
        }
        catch (ApiRequestException exception)
        {
            AssertEqual(400, exception.StatusCode, "fast mode status");
            AssertEqual("unsupported_mode", exception.Code, "fast mode error code");
        }
    }

    return Task.CompletedTask;
}

static Task WebSearchControlsUseToolCallsWithoutBrowserSearch()
{
    var openAi = ProtocolAdapters.FromOpenAi(JsonNode.Parse("""
        { "model": "deepseek-chat2api-expert", "web_search": true, "messages": [{ "role": "user", "content": "search locally" }] }
        """)!.AsObject());
    AssertTrue(!openAi.WebSearch, "OpenAI web search does not toggle the browser");
    AssertEqual("web_search", openAi.Tools![0]!["function"]!["name"]!.GetValue<string>(), "OpenAI web search tool name");
    AssertEqual("web_search", openAi.ToolChoice!["function"]!["name"]!.GetValue<string>(), "OpenAI web search tool choice");

    var anthropic = ProtocolAdapters.FromAnthropic(JsonNode.Parse("""
        { "model": "deepseek-chat2api-expert", "web_search": true, "messages": [{ "role": "user", "content": "search locally" }] }
        """)!.AsObject());
    AssertTrue(!anthropic.WebSearch, "Anthropic web search does not toggle the browser");
    AssertEqual("web_search", anthropic.Tools![0]!["name"]!.GetValue<string>(), "Anthropic web search tool name");
    AssertEqual("web_search", anthropic.ToolChoice!["name"]!.GetValue<string>(), "Anthropic web search tool choice");

    var responses = ProtocolAdapters.FromResponses(JsonNode.Parse("""
        { "model": "deepseek-chat2api-expert", "web_search": true, "input": "search locally" }
        """)!.AsObject());
    AssertTrue(!responses.WebSearch, "Responses web search does not toggle the browser");
    AssertEqual("web_search", responses.Tools![0]!["function"]!["name"]!.GetValue<string>(), "Responses web search tool name");
    AssertEqual("web_search", responses.ToolChoice!["function"]!["name"]!.GetValue<string>(), "Responses web search tool choice");
    return Task.CompletedTask;
}

static Task BrowserRequestsSwitchHeadedLoginContextsToHeadless()
{
    AssertTrue(BrowserContextModePolicy.ShouldSwitchRequestContextToHeadless(contextIsHeadless: false), "headed login context must switch");
    AssertTrue(!BrowserContextModePolicy.ShouldSwitchRequestContextToHeadless(contextIsHeadless: true), "headless request context must be reused");
    return Task.CompletedTask;
}

static Task BrowserModeSelectorTargetsDeepSeekRadioControls()
{
    AssertEqual("[data-model-type='default'][role='radio']", DeepSeekModeSelector.For("fast"), "fast selector");
    AssertEqual("[data-model-type='expert'][role='radio']", DeepSeekModeSelector.For("expert"), "expert selector");
    AssertEqual("[data-model-type='vision'][role='radio']", DeepSeekModeSelector.For("vision"), "vision selector");
    return Task.CompletedTask;
}

static Task BrowserComposerUsesEnabledDeepSeekSendControl()
{
    AssertEqual("div[role='button'].ds-button--primary.ds-button--filled:not(.ds-button--disabled)", DeepSeekComposerSelector.EnabledSendButton, "enabled send selector");
    AssertTrue(!DeepSeekComposerSelector.ShouldReloadAfterNoResponse(hasAttachments: true), "attachments preserve page state");
    AssertTrue(DeepSeekComposerSelector.ShouldReloadAfterNoResponse(hasAttachments: false), "text requests retain refresh recovery");
    return Task.CompletedTask;
}

static async Task ToolRepairLoopFixesMalformedToolEnvelopes()
{
    var request = MakeRequest("repair-loop", [new ApiMessage("user", "call the tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var attempts = new Queue<string>([
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":",
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\"}}]</chat2api_tool_calls>"
    ]);

    var repaired = await ToolRepairLoop.CompleteWithRepairAsync(
        request,
        rawOutput: attempts.Dequeue(),
        sendRepairPromptAsync: async prompt =>
        {
            await Task.Yield();
            AssertContains(prompt, "Repair the malformed tool-call envelope", "repair prompt");
            AssertContains(prompt, "write_file", "repair tool name");
            return attempts.Dequeue();
        },
        CancellationToken.None);

    AssertEqual(1, repaired.ToolCalls?.Count ?? 0, "repaired tool call count");
    AssertEqual("write_file", repaired.ToolCalls![0].Function.Name, "repaired tool call name");
}

static async Task ToolRepairLoopRepairsUndeclaredToolCalls()
{
    var request = MakeRequest("repair-undeclared-tool", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var outputs = new Queue<string>([
        "<chat2api_tool_calls>[{\"name\":\"erase_disk\",\"arguments\":{}}]</chat2api_tool_calls>",
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"safe.txt\"}}]</chat2api_tool_calls>"
    ]);
    var repairs = 0;

    var repaired = await ToolRepairLoop.CompleteWithRepairAsync(
        request,
        outputs.Dequeue(),
        prompt =>
        {
            repairs += 1;
            AssertContains(prompt, "undeclared", "undeclared tool repair reason");
            return Task.FromResult(outputs.Dequeue());
        },
        CancellationToken.None);

    AssertEqual(1, repairs, "undeclared tool repair count");
    AssertEqual("write_file", repaired.ToolCalls![0].Function.Name, "declared repaired tool name");
}

static async Task ToolRepairLoopRepairsInvalidToolArguments()
{
    var request = MakeRequest("repair-invalid-tool-arguments", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var outputs = new Queue<string>([
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":\"not-json\"}]</chat2api_tool_calls>",
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"fixed.txt\"}}]</chat2api_tool_calls>"
    ]);
    var repairs = 0;

    var repaired = await ToolRepairLoop.CompleteWithRepairAsync(
        request,
        outputs.Dequeue(),
        prompt =>
        {
            repairs += 1;
            AssertContains(prompt, "arguments", "invalid arguments repair reason");
            return Task.FromResult(outputs.Dequeue());
        },
        CancellationToken.None);

    AssertEqual(1, repairs, "invalid arguments repair count");
    AssertContains(repaired.ToolCalls![0].Function.Arguments, "fixed.txt", "repaired valid arguments");
}

static async Task ToolRepairLoopRepairsDeclaredSchemaViolations()
{
    var openAiRequest = MakeRequest("repair-openai-schema", [new ApiMessage("user", "write the file")]) with
    {
        Tools = JsonNode.Parse("""
        [{
          "type": "function",
          "function": {
            "name": "write_file",
            "parameters": {
              "type": "object",
              "properties": { "path": { "type": "string" } },
              "required": ["path"],
              "additionalProperties": false
            }
          }
        }]
        """)!.AsArray()
    };
    var openAiRepairs = 0;
    var openAiResult = await ToolRepairLoop.CompleteWithRepairAsync(
        openAiRequest,
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":42}}]</chat2api_tool_calls>",
        _ =>
        {
            openAiRepairs += 1;
            return Task.FromResult("<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"fixed.txt\"}}]</chat2api_tool_calls>");
        },
        CancellationToken.None);
    AssertEqual(1, openAiRepairs, "OpenAI schema repair count");
    AssertContains(openAiResult.ToolCalls![0].Function.Arguments, "fixed.txt", "OpenAI schema repaired arguments");

    var anthropicRequest = MakeRequest("repair-anthropic-schema", [new ApiMessage("user", "look up the status")]) with
    {
        Tools = JsonNode.Parse("""
        [{
          "name": "lookup",
          "input_schema": {
            "type": "object",
            "properties": { "query": { "type": "string" } },
            "required": ["query"],
            "additionalProperties": false
          }
        }]
        """)!.AsArray()
    };
    var anthropicRepairs = 0;
    var anthropicResult = await ToolRepairLoop.CompleteWithRepairAsync(
        anthropicRequest,
        "<chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":{}}]</chat2api_tool_calls>",
        _ =>
        {
            anthropicRepairs += 1;
            return Task.FromResult("<chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":{\"query\":\"status\"}}]</chat2api_tool_calls>");
        },
        CancellationToken.None);
    AssertEqual(1, anthropicRepairs, "Anthropic schema repair count");
    AssertContains(anthropicResult.ToolCalls![0].Function.Arguments, "status", "Anthropic schema repaired arguments");
}

static async Task ProviderStreamingPipelineForwardsObservedDeltas()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("streaming-pipeline", [new ApiMessage("user", "say hello")]);
    var observed = new List<string>();

    var result = await ProviderCompletionPipeline.CompleteStreamingAsync(
        request,
        engine,
        (_, _) => StreamChunks(),
        (_, _) => Task.FromResult(string.Empty),
        delta =>
        {
            observed.Add(delta);
            return Task.CompletedTask;
        },
        warning => throw new InvalidOperationException($"unexpected warning: {warning}"),
        CancellationToken.None);

    AssertEqual("hello world", string.Concat(observed), "forwarded streaming content");
    AssertEqual("hello world", result.Content, "streaming result content");
    var record = await new ConversationStore(dir).LoadAsync("streaming-pipeline");
    AssertEqual("hello world", record.Messages[^1].Content, "stored streaming assistant content");
}

static async Task ProviderCompletionPipelineRepairsMalformedToolEnvelopes()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-repair-loop", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var providerOutputs = new Queue<string>([
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":" ,
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"fixed.txt\"}}]</chat2api_tool_calls>"
    ]);
    var sentPrompts = new List<string>();

    var result = await ProviderCompletionPipeline.CompleteAsync(
        request,
        engine,
        async (prompt, _) =>
        {
            await Task.Yield();
            sentPrompts.Add(prompt);
            return providerOutputs.Dequeue();
        },
        warning => throw new InvalidOperationException($"unexpected warning: {warning}"),
        CancellationToken.None);

    AssertEqual(1, result.ToolCalls?.Count ?? 0, "pipeline repaired tool call count");
    AssertEqual("write_file", result.ToolCalls![0].Function.Name, "pipeline repaired tool call name");
    AssertContains(result.ToolCalls[0].Function.Arguments, "fixed.txt", "pipeline repaired tool args");
    AssertEqual(2, sentPrompts.Count, "pipeline provider prompt count");
    AssertContains(sentPrompts[1], "Repair the malformed tool-call envelope", "pipeline repair prompt");

    var record = await new ConversationStore(dir).LoadAsync("provider-repair-loop");
    AssertContains(record.Messages[^1].Content, "Assistant tool calls:", "pipeline stored assistant tool marker");
    AssertContains(record.Messages[^1].Content, "fixed.txt", "pipeline stored repaired args");
}

static async Task ProviderCompletionPipelinePreservesTextBesideToolCalls()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-tool-text", [new ApiMessage("user", "call lookup and explain")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "lookup" } }]""")!.AsArray()
    };

    var result = await ProviderCompletionPipeline.CompleteAsync(
        request,
        engine,
        (_, _) => Task.FromResult("Before the call. <chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":{\"query\":\"status\"}}]</chat2api_tool_calls> After the call."),
        warning => throw new InvalidOperationException($"unexpected warning: {warning}"),
        CancellationToken.None);

    AssertEqual(1, result.ToolCalls?.Count ?? 0, "text-plus-tool call count");
    AssertContains(result.Content, "Before the call.", "text before tool is preserved");
    AssertContains(result.Content, "After the call.", "text after tool is preserved");
    var record = await new ConversationStore(dir).LoadAsync("provider-tool-text");
    AssertContains(record.Messages[^1].Content, "Before the call.", "stored text before tool");
    AssertContains(record.Messages[^1].Content, "After the call.", "stored text after tool");
}

static async Task ProviderCompletionPipelineRepairsMissingRequiredToolCalls()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-required-tool-repair", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray(),
        ToolChoice = JsonValue.Create("required")
    };
    var providerOutputs = new Queue<string>([
        "I cannot call tools, but here is a summary.",
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"required.txt\"}}]</chat2api_tool_calls>"
    ]);
    var sentPrompts = new List<string>();

    var result = await ProviderCompletionPipeline.CompleteAsync(
        request,
        engine,
        async (prompt, _) =>
        {
            await Task.Yield();
            sentPrompts.Add(prompt);
            return providerOutputs.Dequeue();
        },
        warning => throw new InvalidOperationException($"unexpected warning: {warning}"),
        CancellationToken.None);

    AssertEqual(1, result.ToolCalls?.Count ?? 0, "required tool repair call count");
    AssertEqual("write_file", result.ToolCalls![0].Function.Name, "required tool repair name");
    AssertContains(result.ToolCalls[0].Function.Arguments, "required.txt", "required tool repair arguments");
    AssertEqual(2, sentPrompts.Count, "required tool repair provider prompt count");
    AssertContains(sentPrompts[1], "Tool choice: \"required\"", "required tool repair prompt choice");
}

static async Task ProviderCompletionPipelineRepairsAnthropicAnyToolCalls()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-anthropic-any-tool-repair", [new ApiMessage("user", "call a tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "name": "lookup", "input_schema": { "type": "object" } }]""")!.AsArray(),
        ToolChoice = JsonNode.Parse("""{ "type": "any" }""")
    };
    var providerOutputs = new Queue<string>([
        "I cannot call a tool.",
        "<chat2api_tool_calls>[{\"name\":\"lookup\",\"arguments\":{\"query\":\"repaired\"}}]</chat2api_tool_calls>"
    ]);
    var sentPrompts = new List<string>();

    var result = await ProviderCompletionPipeline.CompleteAsync(
        request,
        engine,
        (prompt, _) =>
        {
            sentPrompts.Add(prompt);
            return Task.FromResult(providerOutputs.Dequeue());
        },
        warning => throw new InvalidOperationException($"unexpected warning: {warning}"),
        CancellationToken.None);

    AssertEqual(1, result.ToolCalls?.Count ?? 0, "Anthropic any repaired tool count");
    AssertEqual("lookup", result.ToolCalls![0].Function.Name, "Anthropic any repaired tool name");
    AssertEqual(2, sentPrompts.Count, "Anthropic any provider prompt count");
    AssertContains(sentPrompts[1], "\"type\":\"any\"", "Anthropic any repair prompt choice");
}

static async Task ProviderCompletionPipelineRejectsUndeclaredToolEnvelope()
{
    var engine = new ContextEngine(CreateTempDirectory(), contextIndex: new UnavailableContextIndex());
    var request = MakeRequest("undeclared-tool-envelope", [new ApiMessage("user", "ordinary answer")]);
    var providerCalls = 0;

    try
    {
        await ProviderCompletionPipeline.CompleteAsync(
            request,
            engine,
            (_, _) =>
            {
                providerCalls += 1;
                return Task.FromResult("<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{}}]</chat2api_tool_calls>");
            },
            _ => { },
            CancellationToken.None);
        throw new InvalidOperationException("undeclared tool envelope was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(502, error.StatusCode, "undeclared tool envelope status");
        AssertEqual("invalid_tool_envelope", error.Code, "undeclared tool envelope code");
    }

    AssertEqual(1, providerCalls, "undeclared tool envelope does not repair");
}

static async Task ProviderCompletionPipelineRejectsToolChoiceNoneEnvelope()
{
    var engine = new ContextEngine(CreateTempDirectory(), contextIndex: new UnavailableContextIndex());
    var request = MakeRequest("tool-choice-none", [new ApiMessage("user", "ordinary answer")]) with
    {
        Tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write_file",
                    ["parameters"] = new JsonObject { ["type"] = "object" }
                }
            }
        },
        ToolChoice = JsonValue.Create("none")
    };
    var providerCalls = 0;

    try
    {
        await ProviderCompletionPipeline.CompleteAsync(
            request,
            engine,
            (_, _) =>
            {
                providerCalls += 1;
                return Task.FromResult("<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{}}]</chat2api_tool_calls>");
            },
            _ => { },
            CancellationToken.None);
        throw new InvalidOperationException("tool choice none envelope was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(502, error.StatusCode, "tool choice none status");
        AssertEqual("invalid_tool_envelope", error.Code, "tool choice none code");
    }

    AssertEqual(1, providerCalls, "tool choice none does not repair");
}

static async Task ProviderCompletionPipelineRejectsMissingRequiredToolCallsAfterRepair()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-required-tool-failed", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray(),
        ToolChoice = JsonValue.Create("required")
    };
    var warnings = new List<string>();
    var providerCalls = 0;

    try
    {
        await ProviderCompletionPipeline.CompleteAsync(
            request,
            engine,
            (_, _) =>
            {
                providerCalls += 1;
                return Task.FromResult("I will only provide normal text.");
            },
            warnings.Add,
            CancellationToken.None);
        throw new InvalidOperationException("missing required tool call was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(502, error.StatusCode, "missing required tool status");
        AssertEqual("invalid_tool_envelope", error.Code, "missing required tool code");
    }

    AssertEqual(3, providerCalls, "missing required tool repair attempts");
    AssertEqual(1, warnings.Count, "missing required tool warning count");
}

static async Task ProviderCompletionPipelineRejectsUnrepairedToolEnvelopes()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("provider-repair-failed", [new ApiMessage("user", "call the write_file tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var warnings = new List<string>();

    try
    {
        await ProviderCompletionPipeline.CompleteAsync(
            request,
            engine,
            (_, _) => Task.FromResult("<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":"),
            warnings.Add,
            CancellationToken.None);
        throw new InvalidOperationException("unrepaired tool envelope was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(502, error.StatusCode, "unrepaired tool envelope status");
        AssertEqual("invalid_tool_envelope", error.Code, "unrepaired tool envelope code");
    }

    AssertEqual(1, warnings.Count, "unrepaired tool warning count");
    var record = await new ConversationStore(dir).LoadAsync("provider-repair-failed");
    AssertEqual(1, record.Messages.Count, "unrepaired tool output not persisted");
}

static async Task ProviderCompletionPipelineRejectsExternalToolSchemaReferences()
{
    var dir = CreateTempDirectory();
    var engine = new ContextEngine(dir);
    var request = MakeRequest("external-schema", [new ApiMessage("user", "call the tool")]) with
    {
        Tools = JsonNode.Parse("""
        [{
          "type": "function",
          "function": {
            "name": "lookup",
            "parameters": { "$ref": "https://example.invalid/schema.json" }
          }
        }]
        """)!.AsArray()
    };
    var providerCalls = 0;

    try
    {
        await ProviderCompletionPipeline.CompleteAsync(
            request,
            engine,
            (_, _) =>
            {
                providerCalls += 1;
                return Task.FromResult("should not call provider");
            },
            _ => { },
            CancellationToken.None);
        throw new InvalidOperationException("external tool schema reference was accepted");
    }
    catch (ApiRequestException error)
    {
        AssertEqual(400, error.StatusCode, "external schema status");
        AssertEqual("invalid_tool_schema", error.Code, "external schema error code");
    }

    AssertEqual(0, providerCalls, "external schema rejects before provider call");
}

static async Task ToolRepairLoopRetriesFailedRepair()
{
    var request = MakeRequest("repair-retry", [new ApiMessage("user", "call the tool")]) with
    {
        Tools = JsonNode.Parse("""[{ "type": "function", "function": { "name": "write_file" } }]""")!.AsArray()
    };
    var outputs = new Queue<string>([
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":" ,
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":" ,
        "<chat2api_tool_calls>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"retry.txt\"}}]</chat2api_tool_calls>"
    ]);
    var repairAttempts = 0;

    var repaired = await ToolRepairLoop.CompleteWithRepairAsync(
        request,
        outputs.Dequeue(),
        async _ =>
        {
            await Task.Yield();
            repairAttempts += 1;
            return outputs.Dequeue();
        },
        CancellationToken.None);

    AssertEqual(2, repairAttempts, "tool repair retry count");
    AssertEqual("write_file", repaired.ToolCalls![0].Function.Name, "retried tool call name");
    AssertContains(repaired.ToolCalls[0].Function.Arguments, "retry.txt", "retried tool call arguments");
}

static ProviderRequest MakeRequest(string conversationId, List<ApiMessage> messages)
{
    return new ProviderRequest(
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Messages: messages,
        Tools: null,
        ToolChoice: null,
        Thinking: false,
        WebSearch: false,
        ConversationId: conversationId,
        MaxTokens: null,
        Temperature: null);
}

static ProviderResult MakeMixedTextToolResult()
{
    return new ProviderResult(
        Id: "result_mixed",
        Model: "deepseek-chat2api-fast",
        Mode: "fast",
        Content: "Before the tool call.\n\nAfter the tool call.",
        ToolCalls: [new ToolCall("call_mixed", "function", new ToolFunction("lookup", "{\"query\":\"status\"}"))],
        Usage: new Usage(1, 1, 2),
        Context: new ContextUsage(0, 0, 0, 0, 0, 1, 0));
}

static async Task<string> RenderSseAsync(Func<HttpResponse, Task> writeAsync)
{
    var context = new DefaultHttpContext();
    await using var body = new MemoryStream();
    context.Response.Body = body;
    await writeAsync(context.Response);
    return System.Text.Encoding.UTF8.GetString(body.ToArray());
}

static async IAsyncEnumerable<string> StreamChunks()
{
    yield return "hello";
    await Task.Yield();
    yield return " world";
}

static string CreateTempDirectory()
{
    var dir = Path.Combine(Path.GetTempPath(), "chat2api-tray-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    return dir;
}

static int GetFreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }
}

static void AssertTrue(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{name}: expected true");
    }
}

static void AssertContains(string actual, string expected, string name)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{name}: expected to contain '{expected}', got '{actual}'");
    }
}

static async Task AssertProviderOfflineAsync(HttpResponseMessage response, string name)
{
    var body = await response.Content.ReadAsStringAsync();
    AssertEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, $"{name} status");
    AssertContains(body, "provider_offline", $"{name} error code");
}

sealed class UnavailableContextIndex : IContextIndex
{
    public int UpsertCalls { get; private set; }

    public Task UpsertAsync(string conversationId, List<ApiMessage> messages, CancellationToken cancellationToken)
    {
        UpsertCalls += 1;
        throw new InvalidOperationException("sqlite vec0 module unavailable");
    }

    public Task<List<ApiMessage>> SearchAsync(string conversationId, string query, int maxMessages, int tokenBudget, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("sqlite vec0 module unavailable");
    }
}

sealed class CapturingContextIndex : IContextIndex
{
    public List<ApiMessage> Indexed { get; } = [];

    public Task UpsertAsync(string conversationId, List<ApiMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            if (!Indexed.Any(existing => existing.Role == message.Role && existing.Content == message.Content && existing.ToolCallId == message.ToolCallId))
            {
                Indexed.Add(message);
            }
        }

        return Task.CompletedTask;
    }

    public Task<List<ApiMessage>> SearchAsync(string conversationId, string query, int maxMessages, int tokenBudget, CancellationToken cancellationToken)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<ApiMessage>();
        var usedTokens = 0;
        foreach (var message in Indexed.Where(message => terms.Any(term => message.Content.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            var remainingTokens = tokenBudget - usedTokens;
            if (remainingTokens <= 0 || matches.Count >= maxMessages)
            {
                break;
            }

            var selected = message;
            if (TokenEstimator.Estimate(selected.Content) > remainingTokens)
            {
                var length = Math.Min(selected.Content.Length, remainingTokens * 4);
                while (length > 1 && TokenEstimator.Estimate(selected.Content[..length]) > remainingTokens)
                {
                    length = Math.Max(1, length / 2);
                }

                selected = selected with { Content = selected.Content[..length] };
            }

            matches.Add(selected);
            usedTokens += TokenEstimator.Estimate(selected.Content);
        }

        return Task.FromResult(matches);
    }
}

sealed class QueuedConversationGate : IConversationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entries;

    public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource SecondQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IAsyncDisposable> EnterAsync(string conversationId, CancellationToken cancellationToken)
    {
        var entry = Interlocked.Increment(ref _entries);
        if (entry == 1)
        {
            await _semaphore.WaitAsync(cancellationToken);
            FirstEntered.TrySetResult();
            await _releaseFirst.Task.WaitAsync(cancellationToken);
            return new ConversationGateLease(_semaphore);
        }

        SecondQueued.TrySetResult();
        await _semaphore.WaitAsync(cancellationToken);
        return new ConversationGateLease(_semaphore);
    }

    public void ReleaseFirst()
    {
        _releaseFirst.TrySetResult();
    }

    private sealed class ConversationGateLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public ConversationGateLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

sealed class FakeWebAdapter : IDeepSeekWebAdapter
{
    public List<string> Prompts { get; } = [];

    public List<IReadOnlyList<ProviderFile>> UploadedFiles { get; } = [];

    public Exception? SendError { get; init; }

    public TimeSpan SendDelay { get; init; }

    public string Response { get; init; } = "MODEL-HOST-COMPLETION";

    public int BeginLoginCalls { get; private set; }

    public int AuthStatusCalls { get; private set; }

    public int SendCalls { get; private set; }

    public int StreamCalls { get; private set; }

    public int CallCount => BeginLoginCalls + AuthStatusCalls + SendCalls + StreamCalls;

    public Task<AuthStatus> BeginLoginAsync(CancellationToken cancellationToken)
    {
        BeginLoginCalls += 1;
        return Task.FromResult(Status());
    }

    public Task<AuthStatus> AuthStatusAsync(string? message, CancellationToken cancellationToken)
    {
        AuthStatusCalls += 1;
        return Task.FromResult(Status(message));
    }

    public Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, CancellationToken cancellationToken)
    {
        return SendAsync(prompt, mode, thinking, webSearch, [], cancellationToken);
    }

    public Task<string> SendAsync(string prompt, string mode, bool thinking, bool webSearch, IReadOnlyList<ProviderFile> files, CancellationToken cancellationToken)
    {
        SendCalls += 1;
        Prompts.Add(prompt);
        UploadedFiles.Add(files);
        if (SendError is not null)
        {
            return Task.FromException<string>(SendError);
        }

        return SendCoreAsync(prompt, cancellationToken);
    }

    private async Task<string> SendCoreAsync(string prompt, CancellationToken cancellationToken)
    {
        if (SendDelay > TimeSpan.Zero)
        {
            await Task.Delay(SendDelay, cancellationToken);
        }

        return prompt.StartsWith("Update the rolling summary", StringComparison.Ordinal)
            ? "MODEL-HOST-SUMMARY"
            : Response;
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, string mode, bool thinking, bool webSearch, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var delta in StreamAsync(prompt, mode, thinking, webSearch, [], cancellationToken))
        {
            yield return delta;
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, string mode, bool thinking, bool webSearch, IReadOnlyList<ProviderFile> files, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StreamCalls += 1;
        yield return await SendAsync(prompt, mode, thinking, webSearch, files, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static AuthStatus Status(string? message = null)
    {
        return new AuthStatus(true, false, "https://chat.deepseek.com", DateTimeOffset.UtcNow.ToString("O"), null, null, message);
    }
}
