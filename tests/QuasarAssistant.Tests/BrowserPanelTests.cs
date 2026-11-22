using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Llm;
using QuasarAssistant.Panel;
using QuasarAssistant.Settings;

namespace QuasarAssistant.Tests;

public sealed class BrowserPanelTests
{
    private static async Task<object?> Call(
        Harness h,
        ClaimsPrincipal user,
        string operation,
        object request
    )
    {
        var op = h
            .Services.GetServices<QuasarOperation>()
            .Single(op => op.OperationId == operation);
        var context = new QuasarOperationContext(user, h.Services);
        var json = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        if (op.Kind != QuasarOperationKind.Upload)
            return await op.InvokeAsync(context, json, CancellationToken.None);
        using var body = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(request, JsonSerializerOptions.Web)
        );
        return await op.InvokeAsync(
            context with
            {
                Kind = op.Kind,
                Upload = body,
                VerifyAccess = _ => Task.CompletedTask,
            },
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None
        );
    }

    [Fact]
    public async Task ConversationIdNeverGrantsAccessToAnotherSubject()
    {
        await using var h = await Harness.StartAsync([]);
        var conversation = h.Conversations.Create("owner", "Owner");
        h.Conversations.Append(
            conversation,
            new ChatMessage { Role = ChatRole.User, Text = "Private" }
        );
        var other = Harness.Person(subject: "other");
        var id = new ItemId(conversation.Id);
        var list = Assert.IsType<ChatIndex>(await Call(h, other, "chat.list", id));
        Assert.Empty(list.Items);
        Assert.True(list.Missing);
        foreach (
            var (operation, request) in new (string, object)[]
            {
                ("chat.read", id),
                ("chat.stop", id),
                ("chat.send", new ChatSend(id.Id, "Hi", null, null)),
            }
        )
        {
            var error = await Assert.ThrowsAsync<QuasarOperationException>(() =>
                Call(h, other, operation, request)
            );
            Assert.Equal(404, error.StatusCode);
        }
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public async Task SettingsRevertTokenIsBoundToTheAccountThatOpenedThePage()
    {
        await using var h = await Harness.StartAsync([]);
        var owner = Harness.Person();
        var id = new ItemId(
            Assert.IsType<string>(await Call(h, owner, "settings.snapshot", new { }))
        );
        foreach (var operation in new[] { "settings.changes", "settings.revert" })
        {
            var error = await Assert.ThrowsAsync<QuasarOperationException>(() =>
                Call(h, Harness.Person(subject: "other"), operation, id)
            );
            Assert.Equal(404, error.StatusCode);
        }
        Assert.Empty(Assert.IsType<List<string>>(await Call(h, owner, "settings.changes", id)));
    }

    [Fact]
    public async Task RedactedFormKeepsWorkerCredentialsAndRefusesAStaleSave()
    {
        await using var h = await Harness.StartAsync([]);
        var backend = h.Backend("test", BackendKind.OpenAi) with
        {
            Headers = new() { ["X-Private"] = "private-header" },
        };
        h.Settings.Save(h.Settings.Current with { Backends = [backend] });
        h.Settings.SetApiKey("test", "private-key");
        h.Settings.SetSandboxToken("private-sandbox");
        var settings = SettingsApi.View(h.Settings).Settings;
        var panel = await Call(h, Harness.Person(), "settings.panel", new { });
        var json = JsonSerializer.Serialize(panel);
        foreach (var secret in new[] { "private-header", "private-key", "private-sandbox" })
            Assert.DoesNotContain(secret, json);
        await SettingsApi.SetBackendsOf(h.Settings, new BackendsSetRequest(settings.Backends));
        Assert.Equal("private-header", h.Settings.Current.Backends.Single().Headers["X-Private"]);
        var change = new SettingsChange(
            settings,
            settings with
            {
                ConversationRetentionDays = 61,
            },
            null,
            null,
            null,
            null,
            []
        );
        SettingsPanelOperations.Save(h.Settings, change);
        Assert.Equal("private-header", h.Settings.Current.Backends.Single().Headers["X-Private"]);
        Assert.Equal("private-key", h.Settings.ApiKey("test"));
        Assert.Equal(
            409,
            Assert
                .Throws<QuasarOperationException>(() =>
                    SettingsPanelOperations.Save(h.Settings, change)
                )
                .StatusCode
        );
    }

    [Fact]
    public void BrowserAssemblyHasNoWorkerDependencies()
    {
        var plugin = new Quasar.AssistantQuasarPlugin();
        var browser = Assert.Single(plugin.GetRazorAssemblies());
        Assert.Equal("QuasarAssistant.Browser", browser.GetName().Name);
        Assert.Equal(browser, Assert.Single(plugin.GetExtensions()).ComponentType.Assembly);
        Assert.DoesNotContain(browser.GetReferencedAssemblies(), a =>
            a.Name is "QuasarAssistant" or "Quasar.Plugin.Abstractions"
        );
    }
}
