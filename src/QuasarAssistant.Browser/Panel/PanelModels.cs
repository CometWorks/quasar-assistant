using System.Text.Json.Serialization;
using QuasarAssistant.Llm;
using QuasarAssistant.Lore;
using QuasarAssistant.Monitoring;
using QuasarAssistant.Settings;
using QuasarAssistant.Skills;
using QuasarAssistant.Storage;

namespace QuasarAssistant.Panel;

public sealed record ItemId(string Id);

public sealed record ChatSend(string? Id, string Text, string? PageUri, string? ServerName);

public sealed record Decision(string Id, bool Approve, string? Reason);

public sealed record PanelStatus(bool Configured, bool CanConfigure);

public sealed record ChatIndex(List<ConversationSummary> Items, int RetentionDays, bool Missing);

public sealed record Card(Proposal Proposal, bool CanApprove);

public sealed record ApprovalList(List<Card> Cards, List<Handover> Handovers, bool CanHandle);

public sealed record ChatLine(
    string Role,
    string Text,
    bool IsError,
    string? ToolName,
    List<CallLine>? ToolCalls,
    Card? Card
);

public sealed record CallLine(string Name, string Arguments);

public sealed record ChatState(
    List<ChatLine> Messages,
    bool Running,
    string? Status,
    string? Draft
);

public sealed record MonitorView(
    bool Enabled,
    string? Error,
    BudgetStatus Budget,
    List<Incident> Incidents
);

public sealed record LoreIndex(List<string> Targets, List<LoreView> Entries);

public sealed record SkillEdit(string Role, string Name, string? Text, bool Create = false);

public sealed record SkillRef(string Role, string Name);

public sealed record WrittenSkill(string Name, string Text, string Summary);

public sealed record ModeOption(string Key, string Area, bool Read);

public sealed record ToolOption(string Name, string Description);

public sealed record ServerTools(
    string Role,
    string Server,
    List<string> Headers,
    List<ToolOption> Tools,
    DateTimeOffset? Time,
    string? Error
);

public sealed record HeaderEdit(
    string Role,
    string Name,
    string? Original,
    Dictionary<string, string>? Headers
);

public sealed record SettingsChange(
    AssistantSettings Before,
    AssistantSettings After,
    string? OriginalBackend,
    string? BackendName,
    string? ApiKey,
    string? SandboxToken,
    List<HeaderEdit> HeaderChanges,
    bool ReplaceBackendHeaders = false
);

public sealed record SettingsPanel(
    AssistantSettings Settings,
    List<string> Keys,
    bool SandboxTokenSet,
    List<ServerTools> Servers,
    Dictionary<string, List<ModeOption>> Operations,
    MetricsSummary Metrics,
    BudgetStatus Budget,
    string PrivacyNotice,
    string BookCommit
);

public sealed record TestResult(bool Ok, string Text);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(ItemId))]
[JsonSerializable(typeof(ChatSend))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(PanelStatus))]
[JsonSerializable(typeof(ChatIndex))]
[JsonSerializable(typeof(ChatState))]
[JsonSerializable(typeof(ApprovalList))]
[JsonSerializable(typeof(MonitorView))]
[JsonSerializable(typeof(LoreIndex))]
[JsonSerializable(typeof(LoreSetRequest))]
[JsonSerializable(typeof(LoreTargetRequest))]
[JsonSerializable(typeof(SkillEdit))]
[JsonSerializable(typeof(SkillRef))]
[JsonSerializable(typeof(SkillSourceSetRequest))]
[JsonSerializable(typeof(List<SkillSourceView>))]
[JsonSerializable(typeof(List<WrittenSkill>))]
[JsonSerializable(typeof(SettingsChange))]
[JsonSerializable(typeof(SettingsPanel))]
[JsonSerializable(typeof(TestResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
internal partial class PanelJson : JsonSerializerContext;
