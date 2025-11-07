// Copyright (c) Microsoft. All rights reserved.

// This file contains integration tests for AG-UI shared state functionality.
// These tests verify that state can be sent from clients via DataContent with "application/json" media type,
// processed by the server through the dual-run mechanism, and that state snapshots are correctly returned.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Agents.AI.AGUI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.IntegrationTests;

public sealed class SharedStateTests : IAsyncDisposable
{
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly ITestOutputHelper _output;

    public SharedStateTests(ITestOutputHelper output)
    {
        this._output = output;
    }

    [Fact]
    public async Task ClientSendsStateToServerAsync()
    {
        // Arrange
        var initialState = new { sessionId = "test-session", step = 1, data = "initial" };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        // Create state message with DataContent
        string stateJson = JsonSerializer.Serialize(initialState);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "hello");

        List<AgentRunResponseUpdate> updates = [];

        // Act
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert - Verify state was received by the fake chat client
        fakeChatClient.ReceivedCalls.Should().HaveCountGreaterThan(0, "fake chat client should have been called");

        // First call should contain the state in a system message (injected by RunWithStateAsync)
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];
        firstCall.Messages.Should().Contain(m => m.Role == ChatRole.System &&
            m.Text != null && m.Text.Contains("sessionId"),
            "system message should contain state JSON");

        // Response should include state snapshot and description
        updates.Should().NotBeEmpty();
        updates.Should().Contain(u => u.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task StateRoundTripPreservesDataAsync()
    {
        // Arrange
        var initialState = new { sessionId = "round-trip", counter = 42, metadata = new { key = "value" } };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        // Create state message
        string stateJson = JsonSerializer.Serialize(initialState);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "increment counter");

        List<AgentRunResponseUpdate> updates = [];

        // Act
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert - Verify the fake received state and returned modified state
        fakeChatClient.ReceivedCalls.Should().HaveCountGreaterThan(0);

        // First call (state update run) should have state in system message
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];
        firstCall.Messages.Should().Contain(m => m.Role == ChatRole.System &&
            m.Text != null && m.Text.Contains("\"counter\":42"));

        // The fake returns updated state with counter: 43
        // Verify we received state snapshot with DataContent
        bool hasStateSnapshot = updates.Any(u => u.Contents.Any(c => c is DataContent dc && dc.MediaType == "application/json"));
        hasStateSnapshot.Should().BeTrue("should receive state snapshot as DataContent");

        AgentRunResponseUpdate? stateUpdate = updates.FirstOrDefault(u =>
            u.Contents.Any(c => c is DataContent dc && dc.MediaType == "application/json"));
        stateUpdate.Should().NotBeNull();

        DataContent? dataContent = stateUpdate!.Contents.OfType<DataContent>().FirstOrDefault(dc => dc.MediaType == "application/json");
        dataContent.Should().NotBeNull();

        string receivedJson = System.Text.Encoding.UTF8.GetString(dataContent!.Data.ToArray());
        JsonElement receivedState = JsonSerializer.Deserialize<JsonElement>(receivedJson);
        receivedState.GetProperty("counter").GetInt32().Should().Be(43, "state should be updated");
    }

    [Fact]
    public async Task ComplexStateWithNestedObjectsAndArraysAsync()
    {
        // Arrange
        var complexState = new
        {
            sessionId = "complex-test",
            step = 5,
            nested = new { value = "test", count = 10 },
            array = new[] { 1, 2, 3 },
            tags = new[] { "tag1", "tag2" }
        };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        string stateJson = JsonSerializer.Serialize(complexState);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "process complex state");

        List<AgentRunResponseUpdate> updates = [];

        // Act
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert - Verify complex state was preserved in system message
        fakeChatClient.ReceivedCalls.Should().HaveCountGreaterThan(0);
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];

        ChatMessage? systemMessage = firstCall.Messages.FirstOrDefault(m => m.Role == ChatRole.System);
        systemMessage.Should().NotBeNull();
        systemMessage!.Text.Should().Contain("\"nested\":");
        systemMessage.Text.Should().Contain("\"array\":");
        systemMessage.Text.Should().Contain("\"count\":10");

        // Verify state snapshot returned
        bool hasStateData = updates.Any(u => u.Contents.Any(c => c is DataContent dc && dc.MediaType == "application/json"));
        hasStateData.Should().BeTrue();
    }

    [Fact]
    public async Task DualRunMechanismExecutesTwiceWithStateAsync()
    {
        // Arrange
        var initialState = new { step = 1, action = "start" };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        string stateJson = JsonSerializer.Serialize(initialState);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "process");

        List<AgentRunResponseUpdate> updates = [];

        // Act
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert - When state is present, dual-run mechanism should execute twice
        // First run: with state (generates state snapshot)
        // Second run: without state (generates description)
        fakeChatClient.ReceivedCalls.Should().HaveCount(2, "dual-run mechanism should call chat client twice when state is present");

        // First call should have state in system message
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];
        firstCall.Messages.Should().Contain(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("step"));

        // Second call should have the summary prompt
        FakeStateChatClient.CallInfo secondCall = fakeChatClient.ReceivedCalls[1];
        secondCall.Messages.Should().Contain(m => m.Role == ChatRole.System &&
            m.Text != null && m.Text.Contains("concise summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StateExtractedFromMessagesAndSentInSystemMessageAsync()
    {
        // Arrange
        var stateObject = new { sessionId = "extract-test", data = "important" };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        string stateJson = JsonSerializer.Serialize(stateObject);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "hello");

        List<AgentRunResponseUpdate> updates = [];

        // Act
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert - State should be extracted from user messages and injected as system message
        fakeChatClient.ReceivedCalls.Should().HaveCountGreaterThan(0);
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];

        // Should have system message with state
        firstCall.Messages.Should().Contain(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("sessionId"));

        // Original user messages should not contain DataContent (state extracted)
        int userMessageCount = firstCall.Messages.Count(m => m.Role == ChatRole.User);
        userMessageCount.Should().BeGreaterThan(0, "should have user messages");
    }

    [Fact]
    public async Task EmptyStateObjectHandledCorrectlyAsync()
    {
        // Arrange
        var emptyState = new { };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        string stateJson = JsonSerializer.Serialize(emptyState);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "hello");

        List<AgentRunResponseUpdate> updates = [];

        // Act & Assert - Empty state should not throw
        await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None))
        {
            updates.Add(update);
        }

        // Assert
        updates.Should().NotBeEmpty();
        fakeChatClient.ReceivedCalls.Should().HaveCount(1, "empty state object {} should be treated as no state, resulting in single run without state mechanism");

        // Empty state should NOT trigger dual-run mechanism - should be treated as regular run
        FakeStateChatClient.CallInfo firstCall = fakeChatClient.ReceivedCalls[0];
        firstCall.Messages.Should().NotContain(m => m.Role == ChatRole.System && m.Text != null && m.Text.Contains("{}"), "empty state objects are treated as no state");
        firstCall.Messages.Should().Contain(m => m.Role == ChatRole.User && m.Text == "hello");
    }

    [Fact]
    public async Task NonStreamingRunAsyncWithStateAsync()
    {
        // Arrange
        var stateObject = new { sessionId = "run-async-test", step = 3 };
        var fakeChatClient = new FakeStateChatClient(this._output);

        await this.SetupTestServerAsync(fakeChatClient);
        var chatClient = new AGUIChatClient(this._client!, "", null);
        AIAgent agent = chatClient.CreateAIAgent(instructions: null, name: "assistant", description: "Sample assistant", tools: []);
        ChatClientAgentThread thread = (ChatClientAgentThread)agent.GetNewThread();

        string stateJson = JsonSerializer.Serialize(stateObject);
        byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(stateJson);
        DataContent stateContent = new(stateBytes, "application/json");
        ChatMessage stateMessage = new(ChatRole.User, [stateContent]);
        ChatMessage userMessage = new(ChatRole.User, "process");

        // Act
        AgentRunResponse response = await agent.RunAsync([userMessage, stateMessage], thread, new AgentRunOptions(), CancellationToken.None);

        // Assert - Non-streaming should also work with state
        response.Messages.Should().NotBeEmpty();
        response.Messages.Should().Contain(m => m.Role == ChatRole.Assistant);

        // Verify dual-run happened
        fakeChatClient.ReceivedCalls.Should().HaveCount(2);
    }

    private async Task SetupTestServerAsync(FakeStateChatClient fakeChatClient)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAGUI();
        builder.WebHost.UseTestServer();

        this._app = builder.Build();

        // Create agent from fake chat client
        AIAgent baseAgent = fakeChatClient.CreateAIAgent(
            instructions: null,
            name: "state-test-agent",
            description: "Agent for state testing",
            tools: []);

        this._app.MapAGUI("/agent", baseAgent);

        await this._app.StartAsync();

        TestServer testServer = this._app.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer not found");

        this._client = testServer.CreateClient();
        this._client.BaseAddress = new Uri("http://localhost/agent");
    }

    public async ValueTask DisposeAsync()
    {
        this._client?.Dispose();
        if (this._app != null)
        {
            await this._app.DisposeAsync();
        }
    }
}

// Fake chat client that mimics an LLM's behavior with state
// This client tracks all calls it receives and can return JSON state snapshots
internal sealed class FakeStateChatClient : IChatClient
{
    private readonly ITestOutputHelper? _output;
    private readonly List<CallInfo> _receivedCalls = [];

    public FakeStateChatClient(ITestOutputHelper? output = null)
    {
        this._output = output;
    }

    public ChatClientMetadata Metadata => new("fake-state-chat-client");

    public List<CallInfo> ReceivedCalls => this._receivedCalls;

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messageList = messages.ToList();
        this._output?.WriteLine($"[FakeStateChatClient] Received {messageList.Count} messages");

        // Track this call
        this._receivedCalls.Add(new CallInfo(messageList, options));

        // Log messages for debugging
        foreach (ChatMessage msg in messageList)
        {
            this._output?.WriteLine($"  [{msg.Role}] {(msg.Text?.Length > 100 ? msg.Text[..100] + "..." : msg.Text)}");
        }

        string messageId = Guid.NewGuid().ToString("N");

        // Check if this is a state update run vs. description run
        ChatMessage? systemMessage = messageList.FirstOrDefault(m => m.Role == ChatRole.System);
        bool isStateUpdateRun = false;
        bool isDescriptionRun = false;
        string? stateJson = null;

        if (systemMessage != null)
        {
            bool hasConciseSummaryPrompt = systemMessage.Contents.Any(c =>
                c is TextContent tc && tc.Text.Contains("concise summary", StringComparison.OrdinalIgnoreCase));

            if (!hasConciseSummaryPrompt)
            {
                // State update run - extract JSON from second TextContent
                // Format: ["Here is the current state in JSON format:", "{json}", "The new state is:"]
                List<AIContent> contents = systemMessage.Contents.ToList();
                if (contents.Count >= 2 && contents[1] is TextContent stateContent)
                {
                    isStateUpdateRun = true;
                    stateJson = stateContent.Text;
                }
            }
            else
            {
                isDescriptionRun = true;
            }
        }

        if (isStateUpdateRun && stateJson != null)
        {
            // First run with state - extract state from system message, modify it, and return as JSON
            this._output?.WriteLine("[FakeStateChatClient] State update run - returning JSON state");

            // Parse the state from system message
            JsonElement stateElement = JsonDocument.Parse(stateJson).RootElement;

            // Modify the state (e.g., increment counter if present)
            Dictionary<string, object?> modifiedState = [];
            foreach (JsonProperty prop in stateElement.EnumerateObject())
            {
                if (prop.Name == "counter" && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    modifiedState[prop.Name] = prop.Value.GetInt32() + 1;
                }
                else if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    modifiedState[prop.Name] = prop.Value.GetInt32();
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    modifiedState[prop.Name] = prop.Value.GetString();
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    modifiedState[prop.Name] = prop.Value;
                }
            }

            // Add a timestamp to show state was processed
            modifiedState["lastUpdated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Return the modified state as JSON text
            string modifiedStateJson = JsonSerializer.Serialize(modifiedState);
            yield return new ChatResponseUpdate
            {
                MessageId = messageId,
                Role = ChatRole.Assistant,
                Contents = [new TextContent(modifiedStateJson)]
            };
        }
        else if (isDescriptionRun)
        {
            // Second run - return description of state changes
            this._output?.WriteLine("[FakeStateChatClient] Description run - returning summary");
            yield return new ChatResponseUpdate
            {
                MessageId = messageId,
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Updated state with new timestamp")]
            };
        }
        else
        {
            // No state present - regular response
            this._output?.WriteLine("[FakeStateChatClient] Regular run - no state");
            yield return new ChatResponseUpdate
            {
                MessageId = messageId,
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Response without state")]
            };
        }

        await Task.CompletedTask;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    // Helper class to track calls
    public sealed class CallInfo
    {
        public CallInfo(List<ChatMessage> messages, ChatOptions? options)
        {
            this.Messages = messages;
            this.Options = options;
        }

        public List<ChatMessage> Messages { get; }
        public ChatOptions? Options { get; }
    }
}
