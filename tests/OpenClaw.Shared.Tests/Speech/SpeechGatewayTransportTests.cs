using System.Reflection;
using System.Text.Json;
using OpenClaw.Shared.Speech;

namespace OpenClaw.Shared.Tests.Speech;

public sealed class SpeechGatewayTransportTests
{
    [Fact]
    public async Task CapabilitiesScopesReadinessToSelectedSessionAndOmitsAbsentSession()
    {
        var (client, proxy) = Create();
        var parameters = new List<JsonElement>();
        proxy.Respond = (method, value) =>
        {
            Assert.Equal("expressive-speech.capabilities", method);
            parameters.Add(value);
            return Task.FromResult(Json("""{"protocol":{"major":1,"minor":0},"available":true,"prepared":true,"live":true}"""));
        };
        using var transport = new SpeechGatewayTransport(client);
        await transport.GetCapabilitiesAsync("agent:main");
        await transport.GetCapabilitiesAsync();
        Assert.Equal("agent:main", parameters[0].GetProperty("sessionKey").GetString());
        Assert.Empty(parameters[1].EnumerateObject());
    }

    [Fact]
    public async Task IntentAndReadUseExistingConnectionWithoutClientSuppliedAuthority()
    {
        var (client, proxy) = Create();
        proxy.Respond = (method, parameters) =>
        {
            if (method == "expressive-speech.intent")
            {
                Assert.Equal(new[] { "delivery", "idempotencyKey", "maxSpeechCharacters", "protocol", "sessionKey" },
                    parameters.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal("run-key", parameters.GetProperty("idempotencyKey").GetString());
                Assert.Equal("live", parameters.GetProperty("delivery").GetString());
                Assert.Equal(2000, parameters.GetProperty("maxSpeechCharacters").GetInt32());
                return Task.FromResult(Json("""{"ticket":"opaque-ticket","expiresAt":12345}"""));
            }
            Assert.Equal("expressive-speech.read", method);
            Assert.Equal(-1, parameters.GetProperty("afterSequence").GetInt32());
            Assert.Equal("opaque-ticket", parameters.GetProperty("ticket").GetString());
            return Task.FromResult(Json("""{"events":[],"state":"pending","retryAfterMs":100}"""));
        };
        using var transport = new SpeechGatewayTransport(client);
        var ticket = await transport.RegisterIntentAsync("main", "run-key", true);
        var batch = await transport.ReadAsync(ticket, -1);
        Assert.Equal("pending", batch.State);
        Assert.Empty(batch.Events);
    }

    [Fact]
    public async Task DisconnectCancelsPendingReadAndReconnectCannotReviveItsTicket()
    {
        var (client, proxy) = Create();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Respond = (_, _) => pending.Task;
        using var transport = new SpeechGatewayTransport(client);
        var read = transport.ReadAsync(new("ticket", 12345), -1);
        proxy.Status?.Invoke(client, ConnectionStatus.Disconnected);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        proxy.Status?.Invoke(client, ConnectionStatus.Connected);
        pending.SetResult(Json("""{"events":[],"state":"pending","retryAfterMs":100}"""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ReadAsync(new("ticket", 12345), -1));
        Assert.Equal(1, proxy.Requests);
    }

    [Theory]
    [InlineData("""{"events":[],"state":"invented","retryAfterMs":100}""")]
    [InlineData("""{"events":[],"state":"active","retryAfterMs":-1}""")]
    [InlineData("""{"events":[{"kind":"segment"}],"state":"active","retryAfterMs":100}""")]
    public async Task MalformedReadNeverBecomesSpeechInput(string response)
    {
        var (client, proxy) = Create();
        proxy.Respond = (_, _) => Task.FromResult(Json(response));
        using var transport = new SpeechGatewayTransport(client);
        await Assert.ThrowsAsync<SpeechProtocolException>(() => transport.ReadAsync(new("ticket", 12345), -1));
    }

    private static (IOperatorGatewayClient, GatewayProxy) Create()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        return (client, (GatewayProxy)(object)client);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public class GatewayProxy : DispatchProxy
    {
        public Func<string, JsonElement, Task<JsonElement>> Respond { get; set; } = (_, _) => throw new NotSupportedException();
        public EventHandler<ConnectionStatus>? Status { get; private set; }
        public int Requests { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_IsConnectedToGateway": case "get_HasHandshakeSnapshot": return true;
                case "add_StatusChanged": Status += (EventHandler<ConnectionStatus>)args![0]!; return null;
                case "remove_StatusChanged": Status -= (EventHandler<ConnectionStatus>)args![0]!; return null;
                case "SendWizardRequestAsync":
                    Requests++;
                    return Respond((string)args![0]!, JsonSerializer.SerializeToElement(args[1]));
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
