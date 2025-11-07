// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI.AGUI.Shared;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.AGUI.UnitTests;

public sealed class ChatResponseUpdateAGUIExtensionsTests
{
    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsRunStartedEvent_ToResponseUpdateWithMetadataAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, updates[0].Role);
        Assert.Equal("run1", updates[0].ResponseId);
        Assert.NotNull(updates[0].CreatedAt);
        Assert.Equal("thread1", updates[0].ConversationId);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsRunFinishedEvent_ToResponseUpdateWithMetadataAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1", Result = JsonSerializer.SerializeToElement("Success") }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Equal(2, updates.Count);
        // First update is RunStarted
        Assert.Equal(ChatRole.Assistant, updates[0].Role);
        Assert.Equal("run1", updates[0].ResponseId);
        // Second update is RunFinished
        Assert.Equal(ChatRole.Assistant, updates[1].Role);
        Assert.Equal("run1", updates[1].ResponseId);
        Assert.NotNull(updates[1].CreatedAt);
        TextContent content = Assert.IsType<TextContent>(updates[1].Contents[0]);
        Assert.Equal("\"Success\"", content.Text); // JSON string representation includes quotes
        // ConversationId is stored in the ChatResponseUpdate
        Assert.Equal("thread1", updates[1].ConversationId);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsRunErrorEvent_ToErrorContentAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunErrorEvent { Message = "Error occurred", Code = "ERR001" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Single(updates);
        Assert.Equal(ChatRole.Assistant, updates[0].Role);
        ErrorContent content = Assert.IsType<ErrorContent>(updates[0].Contents[0]);
        Assert.Equal("Error occurred", content.Message);
        // Code is stored in ErrorCode property
        Assert.Equal("ERR001", content.ErrorCode);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsTextMessageSequence_ToTextUpdatesWithCorrectRoleAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "Hello" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = " World" },
            new TextMessageEndEvent { MessageId = "msg1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));
        Assert.Equal("Hello", ((TextContent)updates[0].Contents[0]).Text);
        Assert.Equal(" World", ((TextContent)updates[1].Contents[0]).Text);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithTextMessageStartWhileMessageInProgress_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "Hello" },
            new TextMessageStartEvent { MessageId = "msg2", Role = AGUIRoles.User }
        ];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
            {
                // Intentionally empty - consuming stream to trigger exception
            }
        });
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithTextMessageEndForWrongMessageId_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "Hello" },
            new TextMessageEndEvent { MessageId = "msg2" }
        ];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
            {
                // Intentionally empty - consuming stream to trigger exception
            }
        });
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_MaintainsMessageContext_AcrossMultipleContentEventsAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "Hello" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = " " },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "World" },
            new TextMessageEndEvent { MessageId = "msg1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Equal(3, updates.Count);
        Assert.All(updates, u => Assert.Equal(ChatRole.Assistant, u.Role));
        Assert.All(updates, u => Assert.Equal("msg1", u.MessageId));
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsToolCallEvents_ToFunctionCallContentAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "GetWeather", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{\"location\":" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "\"Seattle\"}" },
            new ToolCallEndEvent { ToolCallId = "call_1" },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        ChatResponseUpdate toolCallUpdate = updates.First(u => u.Contents.Any(c => c is FunctionCallContent));
        FunctionCallContent functionCall = Assert.IsType<FunctionCallContent>(toolCallUpdate.Contents[0]);
        Assert.Equal("call_1", functionCall.CallId);
        Assert.Equal("GetWeather", functionCall.Name);
        Assert.NotNull(functionCall.Arguments);
        Assert.Equal("Seattle", functionCall.Arguments!["location"]?.ToString());
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithMultipleToolCallArgsEvents_AccumulatesArgsCorrectlyAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "TestTool", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{\"par" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "t1\":\"val" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "ue1\",\"part2" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "\":\"value2\"}" },
            new ToolCallEndEvent { ToolCallId = "call_1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        FunctionCallContent functionCall = updates
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .Single();
        Assert.Equal("value1", functionCall.Arguments!["part1"]?.ToString());
        Assert.Equal("value2", functionCall.Arguments!["part2"]?.ToString());
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithEmptyToolCallArgs_HandlesGracefullyAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "NoArgsTool", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "" },
            new ToolCallEndEvent { ToolCallId = "call_1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        FunctionCallContent functionCall = updates
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .Single();
        Assert.Equal("call_1", functionCall.CallId);
        Assert.Equal("NoArgsTool", functionCall.Name);
        Assert.Null(functionCall.Arguments);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithOverlappingToolCalls_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "Tool1", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{}" },
            new ToolCallStartEvent { ToolCallId = "call_2", ToolCallName = "Tool2", ParentMessageId = "msg1" } // Second start before first ends
        ];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
            {
                // Consume stream to trigger exception
            }
        });
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithMismatchedToolCallId_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "Tool1", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_2", Delta = "{}" } // Wrong call ID
        ];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
            {
                // Consume stream to trigger exception
            }
        });
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithMismatchedToolCallEndId_ThrowsInvalidOperationExceptionAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "Tool1", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{}" },
            new ToolCallEndEvent { ToolCallId = "call_2" } // Wrong call ID
        ];

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
            {
                // Consume stream to trigger exception
            }
        });
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithMultipleSequentialToolCalls_ProcessesAllCorrectlyAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "Tool1", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{\"arg1\":\"val1\"}" },
            new ToolCallEndEvent { ToolCallId = "call_1" },
            new ToolCallStartEvent { ToolCallId = "call_2", ToolCallName = "Tool2", ParentMessageId = "msg2" },
            new ToolCallArgsEvent { ToolCallId = "call_2", Delta = "{\"arg2\":\"val2\"}" },
            new ToolCallEndEvent { ToolCallId = "call_2" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        List<FunctionCallContent> functionCalls = updates
            .SelectMany(u => u.Contents)
            .OfType<FunctionCallContent>()
            .ToList();
        Assert.Equal(2, functionCalls.Count);
        Assert.Equal("call_1", functionCalls[0].CallId);
        Assert.Equal("Tool1", functionCalls[0].Name);
        Assert.Equal("call_2", functionCalls[1].CallId);
        Assert.Equal("Tool2", functionCalls[1].Name);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_ConvertsStateSnapshotEvent_ToDataContentWithJsonAsync()
    {
        // Arrange
        JsonElement stateSnapshot = JsonSerializer.SerializeToElement(new { counter = 42, status = "active" });
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new StateSnapshotEvent { Snapshot = stateSnapshot },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        ChatResponseUpdate stateUpdate = updates.First(u => u.Contents.Any(c => c is DataContent));
        Assert.Equal(ChatRole.Assistant, stateUpdate.Role);
        Assert.Equal("thread1", stateUpdate.ConversationId);
        Assert.Equal("run1", stateUpdate.ResponseId);

        DataContent dataContent = Assert.IsType<DataContent>(stateUpdate.Contents[0]);
        Assert.Equal("application/json", dataContent.MediaType);

        // Verify the JSON content
        string jsonText = System.Text.Encoding.UTF8.GetString(dataContent.Data.ToArray());
        JsonElement deserializedState = JsonSerializer.Deserialize<JsonElement>(jsonText);
        Assert.Equal(42, deserializedState.GetProperty("counter").GetInt32());
        Assert.Equal("active", deserializedState.GetProperty("status").GetString());

        // Verify additional properties
        Assert.NotNull(stateUpdate.AdditionalProperties);
        Assert.True((bool)stateUpdate.AdditionalProperties["is_state_snapshot"]!);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithNullStateSnapshot_DoesNotEmitUpdateAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new StateSnapshotEvent { Snapshot = null },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.DoesNotContain(updates, u => u.Contents.Any(c => c is DataContent));
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithEmptyObjectStateSnapshot_EmitsDataContentAsync()
    {
        // Arrange
        JsonElement emptyState = JsonSerializer.SerializeToElement(new { });
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new StateSnapshotEvent { Snapshot = emptyState },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        ChatResponseUpdate stateUpdate = updates.First(u => u.Contents.Any(c => c is DataContent));
        DataContent dataContent = Assert.IsType<DataContent>(stateUpdate.Contents[0]);
        string jsonText = System.Text.Encoding.UTF8.GetString(dataContent.Data.ToArray());
        Assert.Equal("{}", jsonText);
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithComplexStateSnapshot_PreservesJsonStructureAsync()
    {
        // Arrange
        var complexState = new
        {
            user = new { name = "Alice", age = 30 },
            items = new[] { "item1", "item2", "item3" },
            metadata = new { timestamp = "2024-01-01T00:00:00Z", version = 2 }
        };
        JsonElement stateSnapshot = JsonSerializer.SerializeToElement(complexState);
        List<BaseEvent> events =
        [
            new StateSnapshotEvent { Snapshot = stateSnapshot }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        ChatResponseUpdate stateUpdate = updates.First();
        DataContent dataContent = Assert.IsType<DataContent>(stateUpdate.Contents[0]);
        string jsonText = System.Text.Encoding.UTF8.GetString(dataContent.Data.ToArray());
        JsonElement roundTrippedState = JsonSerializer.Deserialize<JsonElement>(jsonText);

        Assert.Equal("Alice", roundTrippedState.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal(30, roundTrippedState.GetProperty("user").GetProperty("age").GetInt32());
        Assert.Equal(3, roundTrippedState.GetProperty("items").GetArrayLength());
        Assert.Equal("item1", roundTrippedState.GetProperty("items")[0].GetString());
    }

    [Fact]
    public async Task AsChatResponseUpdatesAsync_WithStateSnapshotAndTextMessages_EmitsBothAsync()
    {
        // Arrange
        JsonElement state = JsonSerializer.SerializeToElement(new { step = 1 });
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "Processing..." },
            new TextMessageEndEvent { MessageId = "msg1" },
            new StateSnapshotEvent { Snapshot = state },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in events.ToAsyncEnumerableAsync().AsChatResponseUpdatesAsync(AGUIJsonSerializerContext.Default.Options))
        {
            updates.Add(update);
        }

        // Assert
        Assert.Contains(updates, u => u.Contents.Any(c => c is TextContent));
        Assert.Contains(updates, u => u.Contents.Any(c => c is DataContent));
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_ConvertsTextMessagesToStateSnapshotAsync()
    {
        // Arrange
        List<BaseEvent> stateUpdateEvents =
        [
            new RunStartedEvent { ThreadId = "orig_thread", RunId = "orig_run" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "{\"counter\"" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = ":5,\"active\"" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = ":true}" },
            new TextMessageEndEvent { MessageId = "msg1" },
            new RunFinishedEvent { ThreadId = "orig_thread", RunId = "orig_run" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in stateUpdateEvents.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "new_thread", "new_run", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        Assert.Equal(2, outputEvents.Count);

        RunStartedEvent runStarted = Assert.IsType<RunStartedEvent>(outputEvents[0]);
        Assert.Equal("new_thread", runStarted.ThreadId);
        Assert.Equal("new_run", runStarted.RunId);

        StateSnapshotEvent stateSnapshot = Assert.IsType<StateSnapshotEvent>(outputEvents[1]);
        Assert.NotNull(stateSnapshot.Snapshot);
        Assert.Equal(5, stateSnapshot.Snapshot!.Value.GetProperty("counter").GetInt32());
        Assert.True(stateSnapshot.Snapshot!.Value.GetProperty("active").GetBoolean());

        Assert.DoesNotContain(outputEvents, e => e is RunFinishedEvent);
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_UsesProvidedThreadAndRunIds_NotOriginalAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "original_thread", RunId = "original_run" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "{}" },
            new TextMessageEndEvent { MessageId = "msg1" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "custom_thread", "custom_run", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        RunStartedEvent runStarted = Assert.IsType<RunStartedEvent>(outputEvents[0]);
        Assert.Equal("custom_thread", runStarted.ThreadId);
        Assert.Equal("custom_run", runStarted.RunId);
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_WithToolCalls_YieldsToolCallEventsAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new ToolCallStartEvent { ToolCallId = "call_1", ToolCallName = "GetData", ParentMessageId = "msg1" },
            new ToolCallArgsEvent { ToolCallId = "call_1", Delta = "{\"param\":\"value\"}" },
            new ToolCallEndEvent { ToolCallId = "call_1" },
            new TextMessageStartEvent { MessageId = "msg2", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg2", Delta = "{\"result\":\"ok\"}" },
            new TextMessageEndEvent { MessageId = "msg2" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "thread1", "run1", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        Assert.Contains(outputEvents, e => e is ToolCallStartEvent);
        Assert.Contains(outputEvents, e => e is ToolCallArgsEvent);
        Assert.Contains(outputEvents, e => e is ToolCallEndEvent);
        Assert.Contains(outputEvents, e => e is StateSnapshotEvent);
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_WithRunError_YieldsErrorAndFinishedAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new RunErrorEvent { Message = "Processing failed", Code = "ERR001" },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "thread1", "run1", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        Assert.Equal(3, outputEvents.Count);
        Assert.IsType<RunStartedEvent>(outputEvents[0]);

        RunErrorEvent errorEvent = Assert.IsType<RunErrorEvent>(outputEvents[1]);
        Assert.Equal("Processing failed", errorEvent.Message);
        Assert.Equal("ERR001", errorEvent.Code);

        Assert.IsType<RunFinishedEvent>(outputEvents[2]);
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_WithErrorBeforeFinished_StopsAfterFinishedAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "{\"data\":1}" },
            new RunErrorEvent { Message = "Error", Code = "ERR" },
            new RunFinishedEvent { ThreadId = "thread1", RunId = "run1" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "more text" } // Should not be yielded
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "thread1", "run1", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        Assert.DoesNotContain(outputEvents, e => e is TextMessageContentEvent tce && tce.Delta == "more text");
        Assert.Contains(outputEvents, e => e is RunFinishedEvent);
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_AccumulatesMultipleTextChunks_IntoSingleJsonAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "{" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "\"name\"" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = ":" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "\"test\"" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = ",\"value\":" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "123" },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "}" },
            new TextMessageEndEvent { MessageId = "msg1" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "thread1", "run1", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        StateSnapshotEvent stateSnapshot = outputEvents.OfType<StateSnapshotEvent>().Single();
        Assert.Equal("test", stateSnapshot.Snapshot!.Value.GetProperty("name").GetString());
        Assert.Equal(123, stateSnapshot.Snapshot!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task AsAGUIStateUpdateStreamAsync_FiltersOutTextMessageStartEvent_DoesNotYieldItAsync()
    {
        // Arrange
        List<BaseEvent> events =
        [
            new RunStartedEvent { ThreadId = "thread1", RunId = "run1" },
            new TextMessageStartEvent { MessageId = "msg1", Role = AGUIRoles.Assistant },
            new TextMessageContentEvent { MessageId = "msg1", Delta = "{}" },
            new TextMessageEndEvent { MessageId = "msg1" }
        ];

        // Act
        List<BaseEvent> outputEvents = [];
        await foreach (BaseEvent evt in events.ToAsyncEnumerableAsync().AsAGUIStateUpdateStreamAsync(
            "thread1", "run1", AGUIJsonSerializerContext.Default.Options, default))
        {
            outputEvents.Add(evt);
        }

        // Assert
        Assert.DoesNotContain(outputEvents, e => e is TextMessageStartEvent);
        Assert.DoesNotContain(outputEvents, e => e is TextMessageContentEvent);
        Assert.DoesNotContain(outputEvents, e => e is TextMessageEndEvent);
    }
}
