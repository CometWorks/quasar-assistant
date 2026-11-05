using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using QuasarAssistant.Llm;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Storage;

public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = [];

    public required string Id { get; init; }

    /// <summary>The asker's Quasar subject; only they see the conversation.</summary>
    public required string OwnerSubject { get; init; }
    public string OwnerName { get; init; } = "";
    public string Role { get; init; } = AssistantRoles.Admin;
    public string Title { get; set; } = "New conversation";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; set; }

    public List<ChatMessage> Messages
    {
        get
        {
            lock (_messages)
                return [.. _messages];
        }
        init => _messages = value;
    }

    internal void Add(ChatMessage message)
    {
        lock (_messages)
            _messages.Add(message);
    }
}

public sealed record ConversationSummary(string Id, string Title, DateTimeOffset Updated);

/// <summary>Conversations as JSON files, one folder per asker, deleted some days after their last message.</summary>
public sealed class ConversationStore(AssistantPaths paths, TimeProvider time)
{
    private readonly string _root = Path.Combine(paths.DataDirectory, "conversations");
    private readonly ConcurrentDictionary<string, Conversation> _loaded = new();

    /// <summary>Raised with the conversation ID after every change.</summary>
    public event Action<string>? Changed;

    public Conversation Create(
        string ownerSubject,
        string ownerName,
        string role = AssistantRoles.Admin
    )
    {
        var now = time.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerSubject = ownerSubject,
            OwnerName = ownerName,
            Role = role,
            Created = now,
            Updated = now,
        };
        _loaded[conversation.Id] = conversation;
        return conversation;
    }

    /// <summary>Null unless the conversation exists and belongs to the subject.</summary>
    public Conversation? Get(string ownerSubject, string id)
    {
        if (_loaded.TryGetValue(id, out var loaded))
            return loaded.OwnerSubject == ownerSubject ? loaded : null;
        if (!Guid.TryParseExact(id, "N", out _))
            return null;
        var conversation = JsonFiles.Read<Conversation>(FilePath(ownerSubject, id));
        return conversation is null ? null : _loaded.GetOrAdd(id, conversation);
    }

    public IReadOnlyList<ConversationSummary> List(string ownerSubject)
    {
        var folder = Folder(ownerSubject);
        var saved = Directory.Exists(folder)
            ? Directory
                .EnumerateFiles(folder, "*.json")
                .Select(path => Get(ownerSubject, Path.GetFileNameWithoutExtension(path)))
            : [];
        return saved
            .Concat(_loaded.Values.Where(c => c.OwnerSubject == ownerSubject))
            .OfType<Conversation>()
            .Where(c => c.Messages.Count > 0)
            .DistinctBy(c => c.Id)
            .OrderByDescending(c => c.Updated)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Updated))
            .ToList();
    }

    public void Append(Conversation conversation, ChatMessage message)
    {
        conversation.Add(
            message with
            {
                Time = message.Time == default ? time.GetUtcNow() : message.Time,
            }
        );
        conversation.Updated = time.GetUtcNow();
        if (conversation.Title == "New conversation" && message.Role == ChatRole.User)
            conversation.Title = message.Text.Length > 60 ? message.Text[..60] + "…" : message.Text;
        Save(conversation);
        Changed?.Invoke(conversation.Id);
    }

    /// <summary>Tells the views that something not saved changed, such as the streamed answer.</summary>
    public void Touch(string conversationId) => Changed?.Invoke(conversationId);

    public void Delete(Conversation conversation)
    {
        _loaded.TryRemove(conversation.Id, out _);
        File.Delete(FilePath(conversation.OwnerSubject, conversation.Id));
        Changed?.Invoke(conversation.Id);
    }

    /// <summary>Deletes conversations whose last message is older than the retention.</summary>
    public void Expire(int retentionDays)
    {
        if (!Directory.Exists(_root))
            return;
        var limit = time.GetUtcNow().AddDays(-retentionDays).UtcDateTime;
        foreach (var path in Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories))
            if (File.GetLastWriteTimeUtc(path) < limit)
            {
                _loaded.TryRemove(Path.GetFileNameWithoutExtension(path), out _);
                File.Delete(path);
            }
    }

    private void Save(Conversation conversation)
    {
        lock (conversation)
            JsonFiles.Write(FilePath(conversation.OwnerSubject, conversation.Id), conversation);
    }

    private string FilePath(string ownerSubject, string id) =>
        Path.Combine(Folder(ownerSubject), id + ".json");

    // Subjects can hold any character, so the folder is named after a hash.
    private string Folder(string ownerSubject) =>
        Path.Combine(
            _root,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSubject)))[..32]
        );
}
