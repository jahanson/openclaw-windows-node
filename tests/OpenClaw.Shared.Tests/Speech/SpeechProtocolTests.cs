using System.Text.Json;
using OpenClaw.Shared.Speech;

namespace OpenClaw.Shared.Tests.Speech;

public sealed class SpeechProtocolTests
{
    [Fact]
    public void SharedFixturePreservesCuesUnicodeAndFinalIdentityWithoutReplayingDuplicates()
    {
        var (events, expectedText) = ReadFixture();
        var assembler = new SpeechRenditionAssembler(events[0].Identity);
        Assert.Equal(SpeechDeliveryChange.Began, assembler.Accept(events[0]));
        Assert.Equal(SpeechDeliveryChange.Appended, assembler.Accept(events[1]));
        Assert.Equal(SpeechDeliveryChange.Duplicate, assembler.Accept(events[1]));
        Assert.Equal(SpeechDeliveryChange.Appended, assembler.Accept(events[2]));
        Assert.Equal(SpeechDeliveryChange.Terminal, assembler.Accept(events[3]));
        Assert.Equal(SpeechDeliveryChange.Duplicate, assembler.Accept(events[3]));
        Assert.True(assembler.IsComplete);
        Assert.Equal(expectedText, assembler.Text);
        Assert.Equal("persisted-message-1", assembler.MessageId);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("conflicting-duplicate")]
    [InlineData("different-final")]
    [InlineData("replaced-session")]
    public void ConflictingOrIncompleteDeliveryInvalidatesTheAttempt(string fault)
    {
        var (events, _) = ReadFixture();
        var assembler = new SpeechRenditionAssembler(events[0].Identity);
        assembler.Accept(events[0]);
        assembler.Accept(events[1]);
        var invalid = fault switch
        {
            "gap" => events[3],
            "conflicting-duplicate" => events[1] with { Text = "Different content." },
            "different-final" => events[3] with { Sequence = 2 },
            _ => events[2] with { Identity = events[2].Identity with { SessionIncarnation = "physical-2" } }
        };
        Assert.Throws<SpeechProtocolException>(() => assembler.Accept(invalid));
        Assert.False(assembler.IsComplete);
        Assert.Empty(assembler.Text);
        Assert.Throws<SpeechProtocolException>(() => assembler.Accept(events[2]));
    }

    [Fact]
    public void SessionInvalidationCannotBeReversedByLateEventsOrACompletedRendition()
    {
        var (events, _) = ReadFixture();
        var assembler = new SpeechRenditionAssembler(events[0].Identity);
        foreach (var value in events) assembler.Accept(value);
        assembler.Invalidate();
        Assert.False(assembler.IsComplete);
        Assert.Null(assembler.MessageId);
        Assert.Throws<SpeechProtocolException>(() => assembler.Accept(events[^1]));
    }

    [Theory]
    [InlineData("missing-sequence")]
    [InlineData("missing-minor")]
    [InlineData("unsupported-major")]
    [InlineData("unknown-kind")]
    [InlineData("unnormalized-text")]
    [InlineData("utf8-limit")]
    public void UntrustedWireDataMustMeetTheVersionShapeAndByteContract(string fault)
    {
        var (events, _) = ReadFixture();
        var json = JsonSerializer.SerializeToNode(events[1])!.AsObject();
        switch (fault)
        {
            case "missing-sequence": json.Remove("sequence"); break;
            case "missing-minor": json["protocol"]!.AsObject().Remove("minor"); break;
            case "unsupported-major": json["protocol"]!["major"] = 2; break;
            case "unknown-kind": json["kind"] = "correction"; break;
            case "unnormalized-text": json["text"] = "Before\r\nAfter"; break;
            case "utf8-limit": json["text"] = new string('é', 2049); break;
        }
        using var document = JsonDocument.Parse(json.ToJsonString());
        Assert.Throws<SpeechProtocolException>(() => SpeechProtocol.ParseEvent(document.RootElement));
    }

    [Fact]
    public void CompatibleMinorAndUnknownOptionalFieldsRemainReadable()
    {
        var (events, _) = ReadFixture();
        var json = JsonSerializer.SerializeToNode(events[1])!.AsObject();
        json["protocol"]!["minor"] = 12;
        json["futureHint"] = "optional";
        using var document = JsonDocument.Parse(json.ToJsonString());
        Assert.Equal(events[1].Text, SpeechProtocol.ParseEvent(document.RootElement).Text);
    }

    [Fact]
    public void AggregateTextLimitIsEnforcedEvenForIndividuallyValidSegments()
    {
        var (events, _) = ReadFixture();
        var assembler = new SpeechRenditionAssembler(events[0].Identity);
        assembler.Accept(events[0]);
        for (int sequence = 1; sequence <= 16; sequence++)
            assembler.Accept(events[1] with { Sequence = sequence, Text = new string('a', 4096) });
        Assert.Throws<SpeechProtocolException>(() => assembler.Accept(events[1] with { Sequence = 17, Text = "x" }));
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void IdentityLengthMatchesJsonSchemaUnicodeCodePoints(int scalars, bool valid)
    {
        var (events, _) = ReadFixture();
        var value = events[0] with
        {
            Identity = events[0].Identity with { SessionKey = string.Concat(Enumerable.Repeat("🙂", scalars)) }
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        if (valid) Assert.Equal(value.Identity, SpeechProtocol.ParseEvent(document.RootElement).Identity);
        else Assert.Throws<SpeechProtocolException>(() => SpeechProtocol.ParseEvent(document.RootElement));
    }

    private static (SpeechDeliveryEvent[] Events, string Text) ReadFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Speech", "delivery-fixture.json")));
        return (document.RootElement.GetProperty("events").EnumerateArray().Select(SpeechProtocol.ParseEvent).ToArray(),
            document.RootElement.GetProperty("expectedText").GetString()!);
    }
}
