using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class ElevenLabsDialogClientTests
{
    private static ElevenLabsDialogRequest Request => new() { ApiKey = "private-test-key", VoiceId = "voice" };

    [Fact]
    public async Task Prepared_PreservesCuesAndReturnsCompletePcm()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://api.elevenlabs.io/v1/text-to-dialogue?output_format=pcm_24000", request.RequestUri!.AbsoluteUri);
            Assert.Equal("private-test-key", Assert.Single(request.Headers.GetValues("xi-api-key")));
            return Task.FromResult(Audio([0, 1, 2, 3]));
        });
        using var client = new ElevenLabsDialogClient(handler);
        var result = await client.GeneratePreparedAsync(Request, "[curious] Really?");
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("eleven_v4", body.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("[curious] Really?", body.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString());
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, result.AudioBytes);
        Assert.Equal(24000, result.Format.SampleRate);
    }

    [Theory]
    [InlineData(401, DialogProviderFailure.Authentication)]
    [InlineData(402, DialogProviderFailure.AccountRestriction)]
    [InlineData(403, DialogProviderFailure.AccountRestriction)]
    [InlineData(429, DialogProviderFailure.RateLimited)]
    [InlineData(408, DialogProviderFailure.Timeout)]
    [InlineData(500, DialogProviderFailure.Network)]
    [InlineData(503, DialogProviderFailure.Network)]
    public async Task Prepared_ClassifiesFailureWithoutBodyOrRetry(int status, DialogProviderFailure expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("private-test-key user speech") }));
        using var client = new ElevenLabsDialogClient(handler);
        var error = await Assert.ThrowsAsync<DialogProviderException>(() => client.GeneratePreparedAsync(Request, "Hello"));
        Assert.Equal(expected, error.Reason);
        Assert.DoesNotContain("private-test-key", error.ToString());
        Assert.DoesNotContain("user speech", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Prepared_RejectsEmptyOrUnalignedPcm(int length)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Audio(new byte[length])));
        using var client = new ElevenLabsDialogClient(handler);
        var error = await Assert.ThrowsAsync<DialogProviderException>(() => client.GeneratePreparedAsync(Request, "Hello"));
        Assert.Equal(DialogProviderFailure.InvalidAudio, error.Reason);
    }

    [Fact]
    public async Task Prepared_CancellationStopsPendingRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Audio([0, 0]);
        });
        using var client = new ElevenLabsDialogClient(handler);
        using var cancel = new CancellationTokenSource();
        var request = client.GeneratePreparedAsync(Request, "Hello", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Prepared_RejectsOversizedSpeechBeforeBilling()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Audio([0, 0])));
        using var client = new ElevenLabsDialogClient(handler);
        var error = await Assert.ThrowsAsync<DialogProviderException>(() => client.GeneratePreparedAsync(Request,
            new string('a', ElevenLabsDialogClient.MaxTextCharacters + 1)));
        Assert.Equal(DialogProviderFailure.Limit, error.Reason);
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Audio(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("audio/pcm") } } };

    [Fact]
    public async Task Live_DeliversAudioBeforeInputCompletesAndFinalizesExactlyOnce()
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var socket = await server.AcceptAsync();
            using var setup = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("private-test-key", setup.RootElement.GetProperty("xi_api_key").GetString());
            Assert.Equal("voice", setup.RootElement.GetProperty("voices")[0].GetString());
            using var first = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("[curious] First phrase. ", first.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString());
            // Fragment one JSON frame to prove transport fragmentation is independent of audio chunks.
            var frame = System.Text.Encoding.UTF8.GetBytes("{\"audio\":\"AAABAA==\"}");
            await socket.SendAsync(frame.AsMemory(0, 7), WebSocketMessageType.Text, false, timeout.Token);
            await socket.SendAsync(frame.AsMemory(7), WebSocketMessageType.Text, true, timeout.Token);
            using var second = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("Second phrase.", second.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString());
            using var final = await ReceiveAsync(socket, timeout.Token);
            Assert.True(final.RootElement.GetProperty("close_socket").GetBoolean());
            await SendAsync(socket, "{\"audio\":\"AgADAA==\",\"is_final\":true}", timeout.Token);
        }, timeout.Token);
        async IAsyncEnumerable<string> Text()
        {
            yield return "[curious] First phrase. ";
            await heard.Task.WaitAsync(timeout.Token);
            yield return "Second phrase.";
        }
        using var client = new ElevenLabsDialogClient(new LoopbackTransport(server.Endpoint));
        var chunks = new List<byte[]>();
        await client.StreamAsync(Request, Text(), (audio, _) =>
        {
            chunks.Add(audio.ToArray());
            heard.TrySetResult();
            return ValueTask.CompletedTask;
        }, timeout.Token);
        await serverTask;
        Assert.Equal(2, chunks.Count);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 2, 0, 3, 0 }, chunks.SelectMany(x => x));
    }

    [Fact]
    public async Task Live_RejectsPrematureFinalRatherThanLosingUnsentText()
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var socket = await server.AcceptAsync();
            using var setup = await ReceiveAsync(socket, timeout.Token);
            using var input = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, "{\"audio\":\"AAA=\",\"is_final\":true}", timeout.Token);
            // Let the client process the explicit terminal frame before this server disposes.
            var buffer = new byte[1];
            try { await socket.ReceiveAsync(buffer, timeout.Token); } catch (WebSocketException) { }
        }, timeout.Token);
        async IAsyncEnumerable<string> Text([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return "Only the beginning. ";
            await Task.Delay(Timeout.Infinite, token);
            yield return "Never submitted.";
        }
        using var client = new ElevenLabsDialogClient(new LoopbackTransport(server.Endpoint));
        var error = await Assert.ThrowsAsync<DialogProviderException>(() => client.StreamAsync(Request, Text(),
            (_, _) => ValueTask.CompletedTask, timeout.Token));
        Assert.Equal(DialogProviderFailure.Protocol, error.Reason);
        await serverTask;
    }

    [Theory]
    [InlineData("{\"audio\":123,\"is_final\":true}", DialogProviderFailure.Protocol)]
    [InlineData("{\"audio\":{},\"is_final\":true}", DialogProviderFailure.Protocol)]
    [InlineData("{\"error\":\"authentication_required\",\"message\":\"private provider body\"}", DialogProviderFailure.Authentication)]
    public async Task Live_RejectsInvalidTerminalAfterValidAudio(string terminal, DialogProviderFailure expected)
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(async () =>
        {
            using var socket = await server.AcceptAsync();
            using var setup = await ReceiveAsync(socket, timeout.Token);
            using var input = await ReceiveAsync(socket, timeout.Token);
            using var close = await ReceiveAsync(socket, timeout.Token);
            Assert.True(close.RootElement.GetProperty("close_socket").GetBoolean());
            await SendAsync(socket, "{\"audio\":\"AAA=\"}", timeout.Token);
            await SendAsync(socket, terminal, timeout.Token);
            var buffer = new byte[1];
            try { await socket.ReceiveAsync(buffer, timeout.Token); } catch (WebSocketException) { }
        }, timeout.Token);
        async IAsyncEnumerable<string> Text()
        {
            yield return "Complete speech input.";
            await Task.CompletedTask;
        }
        using var client = new ElevenLabsDialogClient(new LoopbackTransport(server.Endpoint));
        var received = 0;
        var error = await Assert.ThrowsAsync<DialogProviderException>(() => client.StreamAsync(Request, Text(),
            (audio, _) => { received += audio.Length; return ValueTask.CompletedTask; }, timeout.Token));
        Assert.Equal(2, received);
        Assert.Equal(expected, error.Reason);
        Assert.DoesNotContain("private provider body", error.ToString());
        await serverTask;
    }

    [Fact]
    public async Task Live_CancellationReturnsWhileAudioConsumerIgnoresCancellation()
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var socket = await server.AcceptAsync();
            using var setup = await ReceiveAsync(socket, timeout.Token);
            using var input = await ReceiveAsync(socket, timeout.Token);
            using var close = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, "{\"audio\":\"AAA=\"}", timeout.Token);
            try { await socket.ReceiveAsync(new byte[1], timeout.Token); } catch (WebSocketException) { }
        }, timeout.Token);
        static async IAsyncEnumerable<string> Text()
        {
            yield return "Complete input.";
            await Task.CompletedTask;
        }
        using var client = new ElevenLabsDialogClient(new LoopbackTransport(server.Endpoint));
        var streaming = client.StreamAsync(Request, Text(), async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, stop.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streaming.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            try { await streaming.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WrittenReading_PacesProviderSectionsByHardwareDrainAndPreservesUnicodeText()
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var text = string.Concat(Enumerable.Repeat("A complete sentence with cafe\u0301 and 👩‍🚀. ", 75));
        var submitted = new List<string>();
        var outputs = Channel.CreateUnbounded<ReadingOutput>();
        var serverTask = Task.Run(async () =>
        {
            var count = 0;
            while (count < text.Length)
            {
                using var socket = await server.AcceptAsync();
                using var setup = await ReceiveAsync(socket, timeout.Token);
                using var input = await ReceiveAsync(socket, timeout.Token);
                var section = input.RootElement.GetProperty("inputs")[0].GetProperty("text").GetString()!;
                submitted.Add(section);
                count += section.Length;
                using var close = await ReceiveAsync(socket, timeout.Token);
                Assert.True(close.RootElement.GetProperty("close_socket").GetBoolean());
                await SendAsync(socket, JsonSerializer.Serialize(new { audio = Convert.ToBase64String(new byte[12000]), is_final = true }), timeout.Token);
                try { await socket.ReceiveAsync(new byte[1], timeout.Token); } catch (WebSocketException) { }
            }
        }, timeout.Token);
        using var client = new ElevenLabsDialogClient(new LoopbackTransport(server.Endpoint));
        var player = new WasapiPcmPlayback(() => new ReadingOutput(output => outputs.Writer.TryWrite(output)));
        var reading = new WrittenAnswerSpeechPlayback(client, player).PlayAsync(Request, text, timeout.Token);
        var completedText = 0;
        var devices = 0;
        while (completedText < text.Length)
        {
            var device = await outputs.Reader.ReadAsync(timeout.Token);
            Assert.Equal(++devices, submitted.Count);
            Assert.False(reading.IsCompleted);
            var section = submitted[^1];
            Assert.InRange(section.Length, 1, 600);
            completedText += section.Length;
            await device.DrainAsync(timeout.Token);
        }
        await reading;
        await serverTask;
        Assert.True(devices > 1);
        Assert.Equal(text, string.Concat(submitted));
        Assert.All(submitted, section => Assert.False(char.IsLowSurrogate(section[0])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrittenReading_StopOrDeviceFailureNeverSubmitsTheNextSection(bool stop)
    {
        using var server = new SocketServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var connected = 0;
        var serverTask = Task.Run(async () =>
        {
            using var socket = await server.AcceptAsync();
            connected++;
            using var setup = await ReceiveAsync(socket, timeout.Token);
            using var input = await ReceiveAsync(socket, timeout.Token);
            using var close = await ReceiveAsync(socket, timeout.Token);
            await SendAsync(socket, JsonSerializer.Serialize(new { audio = Convert.ToBase64String(new byte[12000]), is_final = true }), timeout.Token);
            try { await socket.ReceiveAsync(new byte[1], timeout.Token); } catch (WebSocketException) { }
        }, timeout.Token);
        var started = new TaskCompletionSource<ReadingOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new ElevenLabsDialogClient(new CountingTransport(server.Endpoint, () => Interlocked.Increment(ref requests)));
        var player = new WasapiPcmPlayback(() => new ReadingOutput(output => started.TrySetResult(output)));
        var reading = new WrittenAnswerSpeechPlayback(client, player).PlayAsync(Request, new string('x', 2731), cancellation.Token);
        var device = await started.Task.WaitAsync(timeout.Token);
        if (stop)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(timeout.Token));
        }
        else
        {
            device.Fail();
            await Assert.ThrowsAsync<IOException>(() => reading.WaitAsync(timeout.Token));
        }
        await serverTask;
        Assert.Equal(1, connected);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(10001, false)]
    [InlineData(601, true)]
    public async Task WrittenReading_RejectsOversizeTextOrUnsplittableGraphemeBeforeBilling(int size, bool combining)
    {
        using var handler = new Handler((_, _) => throw new Exception("Must not contact provider"));
        using var client = new ElevenLabsDialogClient(handler);
        var text = combining ? "a" + new string('\u0301', size) : new string('x', size);
        var reader = new WrittenAnswerSpeechPlayback(client, new WasapiPcmPlayback(() => throw new Exception("Must not open device")));
        var error = await Assert.ThrowsAsync<WrittenAnswerReadingException>(() => reader.PlayAsync(Request, text, CancellationToken.None));
        Assert.Equal("written-answer-text-limit", error.Reason);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class ReadingOutput(Action<ReadingOutput> started) : IWavePlayer, IWavePosition
    {
        private IWaveProvider? _provider;
        private long _position;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public PlaybackState PlaybackState { get; private set; }
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat => _provider!.WaveFormat;
        public void Init(IWaveProvider provider) => _provider = provider;
        public void Play() { PlaybackState = PlaybackState.Playing; started(this); }
        public void Stop() => PlaybackState = PlaybackState.Stopped;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Dispose() { }
        public long GetPosition() => _position;
        public void Fail() => PlaybackStopped?.Invoke(this, new(new IOException("Device removed")));
        public async Task DrainAsync(CancellationToken token)
        {
            var buffer = new byte[12000];
            while (_provider!.Read(buffer, 0, buffer.Length) != 0)
            {
                token.ThrowIfCancellationRequested();
                _position = long.MaxValue;
                await Task.Yield();
            }
            PlaybackStopped?.Invoke(this, new(null));
        }
    }

    private sealed class CountingTransport(Uri endpoint, Action count) : DelegatingHandler(new LoopbackTransport(endpoint))
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            count();
            return base.SendAsync(request, token);
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(message.ToArray());
    }

    private static ValueTask SendAsync(WebSocket socket, string message, CancellationToken token) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(message).AsMemory(), WebSocketMessageType.Text, true, token);

    private sealed class SocketServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Uri Endpoint { get; }
        public SocketServer()
        {
            var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{port}/");
        }
        public async Task<WebSocket> AcceptAsync()
        {
            var context = await _listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (await context.AcceptWebSocketAsync(null)).WebSocket;
        }
        public void Dispose() => _listener.Close();
    }

    private sealed class LoopbackTransport(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("api.elevenlabs.io", request.RequestUri!.Host);
            Assert.Equal("/v1/text-to-dialogue/stream-input", request.RequestUri.AbsolutePath);
            Assert.False(request.Headers.Contains("xi-api-key"));
            request.RequestUri = new UriBuilder(endpoint) { Scheme = "http" }.Uri;
            return base.SendAsync(request, token);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await send(request, cancellationToken);
        }
    }
}
