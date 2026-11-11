using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;

namespace QuasarAssistant.Lore;

public sealed record LoreListRequest;

/// <summary>The body of <c>PUT /api/v1/assistant/lore/{target}</c>.</summary>
public sealed record LoreBody(
    string Kind,
    string? Text = null,
    string? GitUrl = null,
    string? Token = null
);

/// <summary>
/// The lore operations and their REST API. They need <c>CanEditServers</c>, are audited by core, and are on the
/// agents' deny list: no agent can read or change a lore, admin-only pages and tokens included.
/// </summary>
public static class LoreOperations
{
    public const string List = "lore.list";
    public const string Set = "lore.set";
    public const string Refresh = "lore.refresh";
    public const string Route = "/api/v1/assistant/lore";

    private static readonly string[] Policies = [QuasarPolicyNames.CanEditServers];
    private static readonly TimeSpan FetchDeadline = GitFetcher.Timeout + TimeSpan.FromSeconds(30);

    public static IServiceCollection AddLoreOperations(this IServiceCollection services)
    {
        var plugin = AssistantServiceCollectionExtensions.PluginId;
        services.AddQuasarOperation<LoreListRequest, IReadOnlyList<LoreView>>(
            plugin,
            List,
            Policies,
            (context, _, _) =>
                Task.FromResult(context.Services.GetRequiredService<LoreStore>().List()),
            new QuasarOperationMetadata
            {
                Effect = QuasarOperationEffect.Read,
                AgentAccess = QuasarAgentAccess.Denied,
                Description = "Lists the lore of each server and cluster for the assistant.",
            }
        );
        services.AddQuasarOperation<LoreSetRequest, LoreView?>(
            plugin,
            Set,
            Policies,
            (context, request, token) =>
                Invalid(() =>
                    context.Services.GetRequiredService<LoreStore>().SetAsync(request, token)
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Deadline = FetchDeadline,
                Description =
                    "Sets a server's or cluster's lore for the assistant: free text, a Git skill URL, or none.",
            }
        );
        services.AddQuasarOperation<LoreTargetRequest, LoreView?>(
            plugin,
            Refresh,
            Policies,
            async (context, request, token) =>
                await context
                    .Services.GetRequiredService<LoreStore>()
                    .RefreshAsync(request.Target, token)
                ?? throw new QuasarOperationException(
                    404,
                    "That server or cluster has no Git lore."
                ),
            new QuasarOperationMetadata
            {
                AgentAccess = QuasarAgentAccess.Denied,
                Reversal = QuasarReversal.NothingToReverse,
                Deadline = FetchDeadline,
                Description = "Fetches a server's or cluster's Git lore skill again.",
            }
        );
        return services;
    }

    /// <summary>JSON routes for scripts and the CLI, with a bearer token or a session; each one runs the operation
    /// through core's dispatcher.</summary>
    public static void MapLoreApi(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup(Route).RequireAuthorization();
        // A cast: as a plain RequestDelegate the result would be dropped.
        routes.MapGet(
            "",
            (Delegate)((HttpContext http) => Dispatch(http, List, new LoreListRequest()))
        );
        routes.MapPut(
            "/{target}",
            (string target, LoreBody body, HttpContext http) =>
                Dispatch(
                    http,
                    Set,
                    new LoreSetRequest(target, body.Kind, body.Text, body.GitUrl, body.Token)
                )
        );
        routes.MapDelete(
            "/{target}",
            (string target, HttpContext http) =>
                Dispatch(http, Set, new LoreSetRequest(target, LoreKinds.None))
        );
        routes.MapPost(
            "/{target}/refresh",
            (string target, HttpContext http) =>
                Dispatch(http, Refresh, new LoreTargetRequest(target))
        );
    }

    private static async Task<T> Invalid<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (ArgumentException error)
        {
            throw new QuasarOperationException(400, error.Message);
        }
    }

    private static async Task<IResult> Dispatch(HttpContext http, string operation, object request)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        var dispatcher = http.RequestServices.GetRequiredService<IQuasarOperationDispatcher>();
        var result = await dispatcher.DispatchAsync(
            new QuasarOperationContext(http.User, http.RequestServices),
            AssistantServiceCollectionExtensions.PluginId,
            operation,
            JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web),
            http.RequestAborted
        );
        return result.ToHttpResult();
    }
}
