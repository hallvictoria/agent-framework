// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

/// <summary>
/// Provides extension methods for mapping AG-UI agents to ASP.NET Core endpoints.
/// </summary>
public static class AGUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps an AG-UI agent endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The URL pattern for the endpoint.</param>
    /// <param name="aiAgent">The agent instance.</param>
    /// <returns>An <see cref="IEndpointConventionBuilder"/> for the mapped endpoint.</returns>
    public static IEndpointConventionBuilder MapAGUI(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("route")] string pattern,
        AIAgent aiAgent)
    {
        return endpoints.MapPost(pattern, async ([FromBody] RunAgentInput? input, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
            var jsonSerializerOptions = jsonOptions.Value.SerializerOptions;

            var messages = input.Messages.AsChatMessages(jsonSerializerOptions);
            var agent = aiAgent;

            var clientTools = input.Tools?.AsAITools().ToList();
            var baseOptions = CreateBaseAgentRunOptions(clientTools);

            IAsyncEnumerable<BaseEvent>? events = null;
            var hasState = !input.State.Equals(default);
            if (hasState)
            {
                events = RunWithStateAsync(agent, baseOptions, messages, clientTools, input, jsonSerializerOptions, cancellationToken);
            }
            else
            {
                events = RunWithoutStateAsync(agent, baseOptions, messages, clientTools, input, jsonSerializerOptions, cancellationToken);
            }

            var sseLogger = context.RequestServices.GetRequiredService<ILogger<AGUIServerSentEventsResult>>();
            return new AGUIServerSentEventsResult(events, sseLogger);
        });
    }

    private static ChatClientAgentRunOptions? CreateBaseAgentRunOptions(List<AITool>? clientTools)
    {
        if (clientTools?.Count > 0)
        {
            return new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Tools = clientTools
                }
            };
        }

        return null;
    }

    private static async IAsyncEnumerable<BaseEvent> RunWithStateAsync(
        AIAgent agent,
        ChatClientAgentRunOptions? noStateRunOptions,
        IEnumerable<ChatMessage> messages,
        List<AITool>? clientTools,
        RunAgentInput input,
        JsonSerializerOptions jsonSerializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // We are going to do two runs:
        // The first run will update the state.
        // The second will provide a description of the state update.
        // We are going to stitch the two streams together as one for the client.
        var runWithStateOptions = noStateRunOptions?.ChatOptions != null
            ? new ChatClientAgentRunOptions
            {
                ChatOptions = noStateRunOptions.ChatOptions.Clone()
            }
            : new ChatClientAgentRunOptions()
            {
                ChatOptions = new()
                {
                    Tools = clientTools,
                    ResponseFormat = ChatResponseFormat.Json
                }
            };

        if (noStateRunOptions?.ChatOptions != null)
        {
            runWithStateOptions.ChatOptions!.ResponseFormat = ChatResponseFormat.Json;
        }

        messages = messages.Append(new ChatMessage(
            ChatRole.System,
            [
                new TextContent("Here is the current state in JSON format:"),
                        new TextContent(input.State.GetRawText()),
                        new TextContent("The new state is:")
            ]));

        // We are going to coolect the chat response updates to generate a new list of messages
        // that we are going to pass on the second run.
        var accumulator = new List<ChatResponseUpdate>();
        var stateUpdateStream = agent.RunStreamingAsync(
            messages,
            options: runWithStateOptions,
            cancellationToken: cancellationToken)
            .AsChatResponseUpdatesAsync()
            .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
            .CollectUpdatesAsync(accumulator, cancellationToken)
            .AsAGUIEventStreamAsync(
                input.ThreadId,
                input.RunId,
                jsonSerializerOptions,
                cancellationToken)
            .AsAGUIStateUpdateStreamAsync(
                input.ThreadId,
                input.RunId,
                jsonSerializerOptions,
                cancellationToken);

        bool emittedState = false;
        await foreach (var update in stateUpdateStream.ConfigureAwait(false))
        {
            if (update is StateSnapshotEvent)
            {
                emittedState = true;
            }
            yield return update;
        }

        if (!emittedState)
        {
            // We got here because there was an error or a client tool call during the state update.
            yield break;
        }

        // At this point we have new messages to pass to the second run
        var response = accumulator.ToChatResponse();
        messages = messages.Concat(response.Messages);
        messages = messages.Append(new ChatMessage(
            ChatRole.System,
            [new TextContent("Please provide a concise summary of the state changes in at most two sentences.")]));

        await foreach (var update in RunWithoutStateAsync(
            agent,
            noStateRunOptions,
            messages,
            clientTools,
            input,
            jsonSerializerOptions,
            cancellationToken).ConfigureAwait(false))
        {
            if (update is RunStartedEvent)
            {
                // Skip the second run started event
                continue;
            }

            yield return update;
        }
    }

    private static IAsyncEnumerable<BaseEvent> RunWithoutStateAsync(
        AIAgent agent,
        ChatClientAgentRunOptions? runOptions,
        IEnumerable<ChatMessage> messages,
        List<AITool>? clientTools,
        RunAgentInput input,
        JsonSerializerOptions jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        return agent.RunStreamingAsync(
            messages,
            options: runOptions,
            cancellationToken: cancellationToken)
            .AsChatResponseUpdatesAsync()
            .FilterServerToolsFromMixedToolInvocationsAsync(clientTools, cancellationToken)
            .AsAGUIEventStreamAsync(
                input.ThreadId,
                input.RunId,
                jsonSerializerOptions,
                cancellationToken);
    }
}
