using System.Text.Json;
using System.Text.Json.Nodes;
using Finmind.Api.Finance;

namespace Finmind.Finance.Tests;

// ADR-0013 B2: the SSE wire format and the resume cursor.
public sealed class EventStreamTests
{
    [Fact]
    public void One_event_is_one_message_with_its_sequence_as_id()
    {
        var evt = new RunEvent(Guid.NewGuid(), 42, "TOOL_RETURNED", 1, DateTimeOffset.UnixEpoch, "agent", "tool_call", "ok",
            null, null, null, null, new JsonObject { ["text"] = "line one\nline two" }, "ab", null, "turn-0001");
        var message = EventStream.Format(evt);
        Assert.StartsWith("id: 42\nevent: TOOL_RETURNED\ndata: ", message);
        Assert.EndsWith("\n\n", message);
        var data = message["id: 42\nevent: TOOL_RETURNED\ndata: ".Length..^2];
        Assert.DoesNotContain('\n', data); // a newline inside data would split the message
        Assert.Equal(42, JsonNode.Parse(data)!["sequence"]!.GetValue<long>());
        Assert.Equal("line one\nline two", (string)JsonNode.Parse(data)!["payload"]!["text"]!);
    }

    [Theory]
    [InlineData(null, null, 0L)]
    [InlineData(null, "7", 7L)]
    [InlineData("12", "7", 12L)]
    [InlineData(" 12 ", null, 12L)]
    [InlineData("-1", null, null)]
    [InlineData("x", null, null)]
    [InlineData(null, "1.5", null)]
    [InlineData("99999999999999999999", null, null)]
    public void Resume_cursor(string? lastEventId, string? afterSequence, long? expected) =>
        Assert.Equal(expected, EventStream.Cursor(lastEventId, afterSequence));

    [Fact]
    public void End_message_carries_status_and_last_sequence()
    {
        var message = EventStream.End("COMPLETED", 9);
        Assert.StartsWith("event: end\ndata: ", message);
        var data = JsonNode.Parse(message["event: end\ndata: ".Length..^2])!;
        Assert.Equal(("COMPLETED", 9L), ((string)data["status"]!, data["lastSequence"]!.GetValue<long>()));
    }
}
