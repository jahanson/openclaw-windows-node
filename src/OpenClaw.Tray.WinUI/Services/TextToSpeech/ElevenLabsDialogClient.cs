using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace OpenClawTray.Services;

public sealed class ElevenLabsDialogRequest
{
    public required string ApiKey { get; init; }
    public required string VoiceId { get; init; }
    public string CueFormat { get; init; } = "elevenlabs-audio-tags";
}

public sealed record PreparedDialogAudio(byte[] AudioBytes, PcmAudioFormat Format);

public enum DialogProviderFailure
{
    Configuration, Authentication, AccountRestriction, RateLimited, Timeout,
    InvalidRequest, InvalidAudio, Protocol, Limit, Network
}

public sealed class DialogProviderException(DialogProviderFailure reason, string message) : Exception(message)
{
    public DialogProviderFailure Reason { get; } = reason;
}

/// <summary>
/// Single-voice v4 dialogue transport. Each invocation owns its connection and never retries synthesis.
/// Audio callbacks apply backpressure and must honor cancellation. No credentials or provider bodies enter errors.
/// </summary>
public sealed class ElevenLabsDialogClient : IDisposable
{
    public const int MaxTextCharacters = 2000;
    public const int MaxAudioBytes = 32 * 1024 * 1024;
    private const int MaxFrameBytes = 1024 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public static PcmAudioFormat AudioFormat { get; } = new(24000);
    private readonly HttpClient _http;
    private static readonly Uri StreamEndpoint = new("wss://api.elevenlabs.io/v1/text-to-dialogue/stream-input?model_id=eleven_v4_turbo&output_format=pcm_24000");

    public ElevenLabsDialogClient() : this(new SocketsHttpHandler
        { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { }

    public ElevenLabsDialogClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<PreparedDialogAudio> GeneratePreparedAsync(
        ElevenLabsDialogRequest request, string text, CancellationToken cancellationToken = default)
    {
        Validate(request);
        ValidateText(text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post,
                "https://api.elevenlabs.io/v1/text-to-dialogue?output_format=pcm_24000");
            message.Headers.Add("xi-api-key", request.ApiKey);
            message.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model_id = "eleven_v4", inputs = new[] { new { text, voice_id = request.VoiceId } }
            }), System.Text.Encoding.UTF8, "application/json");
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            headersDeadline.CancelAfter(IdleTimeout);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType != "audio/pcm")
                throw Fail(DialogProviderFailure.InvalidAudio, "ElevenLabs returned an unexpected audio format.");
            if (response.Content.Headers.ContentLength > MaxAudioBytes)
                throw Fail(DialogProviderFailure.Limit, "ElevenLabs audio exceeded the size limit.");
            using var audio = new MemoryStream();
            using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[32768];
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                idle.CancelAfter(IdleTimeout);
                var count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (audio.Length + count > MaxAudioBytes)
                    throw Fail(DialogProviderFailure.Limit, "ElevenLabs audio exceeded the size limit.");
                audio.Write(buffer, 0, count);
            }
            if (audio.Length == 0 || audio.Length % AudioFormat.BlockAlign != 0)
                throw Fail(DialogProviderFailure.InvalidAudio, "ElevenLabs returned empty or incomplete PCM audio.");
            return new(audio.ToArray(), AudioFormat);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Fail(DialogProviderFailure.Timeout, "ElevenLabs generation timed out."); }
        catch (HttpRequestException)
        { throw Fail(DialogProviderFailure.Network, "The ElevenLabs connection failed."); }
    }

    public async Task StreamAsync(ElevenLabsDialogRequest request, IAsyncEnumerable<string> committedText,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onAudio,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        ArgumentNullException.ThrowIfNull(committedText);
        ArgumentNullException.ThrowIfNull(onAudio);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        var operationToken = deadline.Token;
        using var socket = new ClientWebSocket();
        // Explicit handler disables redirects for the upgrade too. Credentials are sent only after upgrading.
        using var sends = new SemaphoreSlim(1);
        Task? sender = null;
        Task? receiver = null;
        var inputClosed = false;
        try
        {
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(StreamEndpoint,
                _http, connectDeadline.Token).ConfigureAwait(false);
            await SendAsync(new { voices = new[] { request.VoiceId }, xi_api_key = request.ApiKey }).ConfigureAwait(false);
            sender = SendTextAsync();
            receiver = ReceiveAudioAsync();
            // A failure in either direction cancels the other before awaiting its cleanup.
            var first = await Task.WhenAny(sender, receiver).WaitAsync(operationToken).ConfigureAwait(false);
            await first.WaitAsync(operationToken).ConfigureAwait(false);
            await Task.WhenAll(sender, receiver).WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw Fail(DialogProviderFailure.Timeout, "ElevenLabs streaming timed out."); }
        catch (WebSocketException)
        { throw Fail(DialogProviderFailure.Network, "The ElevenLabs streaming connection failed."); }
        catch (JsonException)
        { throw Fail(DialogProviderFailure.Protocol, "ElevenLabs returned a malformed streaming message."); }
        finally
        {
            deadline.Cancel();
            socket.Abort();
            // A hostile/noncooperative producer must not keep the UI waiting indefinitely during Stop.
            // The cancelled token and aborted socket prevent any subsequent send or audio delivery.
            if (sender is not null) await ObserveCleanupAsync(sender).ConfigureAwait(false);
            if (receiver is not null) await ObserveCleanupAsync(receiver).ConfigureAwait(false);
        }

        async Task SendAsync(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            await sends.WaitAsync(operationToken).ConfigureAwait(false);
            try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, operationToken).ConfigureAwait(false); }
            finally { sends.Release(); }
        }

        async Task SendTextAsync()
        {
            var length = 0;
            await using var iterator = committedText.GetAsyncEnumerator(operationToken);
            while (true)
            {
                var next = iterator.MoveNextAsync().AsTask();
                while (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(8), operationToken)).ConfigureAwait(false) != next)
                {
                    operationToken.ThrowIfCancellationRequested();
                    await SendAsync(new { keep_alive = true }).ConfigureAwait(false);
                }
                if (!await next.ConfigureAwait(false)) break;
                var text = iterator.Current;
                if (string.IsNullOrEmpty(text)) continue;
                length = checked(length + text.Length);
                if (length > MaxTextCharacters)
                    throw Fail(DialogProviderFailure.Limit, "Dialog speech exceeds the 2000 character limit.");
                await SendAsync(new { inputs = new[] { new { text, voice_id = request.VoiceId, new_turn = false } } }).ConfigureAwait(false);
            }
            if (length == 0) throw Fail(DialogProviderFailure.InvalidRequest, "Dialog speech is empty.");
            Volatile.Write(ref inputClosed, true);
            await SendAsync(new { close_socket = true }).ConfigureAwait(false);
        }

        async Task ReceiveAudioAsync()
        {
            var buffer = new byte[32768];
            var total = 0;
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult read;
                do
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                    idle.CancelAfter(IdleTimeout);
                    read = await socket.ReceiveAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                    if (read.MessageType != WebSocketMessageType.Text)
                        throw Fail(DialogProviderFailure.Protocol, "ElevenLabs closed before completing the audio stream.");
                    if (message.Length + read.Count > MaxFrameBytes)
                        throw Fail(DialogProviderFailure.Limit, "ElevenLabs streaming message exceeded the size limit.");
                    message.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw Fail(DialogProviderFailure.Protocol, "ElevenLabs returned an unexpected streaming message.");
                if (root.TryGetProperty("error", out var error))
                    throw StreamingFailure(error);
                if (root.TryGetProperty("audio", out var audio) && audio.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw Fail(DialogProviderFailure.Protocol, "ElevenLabs returned an invalid audio field.");
                if (audio.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(audio.GetString()))
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(audio.GetString()!); }
                    catch (FormatException) { throw Fail(DialogProviderFailure.InvalidAudio, "ElevenLabs returned malformed audio."); }
                    total = checked(total + bytes.Length);
                    if (total > MaxAudioBytes) throw Fail(DialogProviderFailure.Limit, "ElevenLabs audio exceeded the size limit.");
                    if (bytes.Length % AudioFormat.BlockAlign != 0)
                        throw Fail(DialogProviderFailure.InvalidAudio, "ElevenLabs returned incomplete PCM audio.");
                    operationToken.ThrowIfCancellationRequested();
                    await onAudio(bytes, operationToken).ConfigureAwait(false);
                }
                if (root.TryGetProperty("is_final", out var final) && final.ValueKind == JsonValueKind.True)
                {
                    if (!Volatile.Read(ref inputClosed))
                        throw Fail(DialogProviderFailure.Protocol, "ElevenLabs ended audio before all speech was submitted.");
                    if (total == 0) throw Fail(DialogProviderFailure.InvalidAudio, "ElevenLabs returned empty audio.");
                    return;
                }
            }
        }
    }

    private static void Validate(ElevenLabsDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.VoiceId))
            throw Fail(DialogProviderFailure.Configuration, "Configure the ElevenLabs API key and voice before using Dialog.");
        if (request.CueFormat != "elevenlabs-audio-tags")
            throw Fail(DialogProviderFailure.Configuration, "This speech cue format is not supported by ElevenLabs Dialog.");
    }

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Fail(DialogProviderFailure.InvalidRequest, "Dialog speech is empty.");
        if (text.Length > MaxTextCharacters) throw Fail(DialogProviderFailure.Limit, "Dialog speech exceeds the 2000 character limit.");
    }

    private static DialogProviderException HttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => Fail(DialogProviderFailure.Authentication, "ElevenLabs rejected the API key. Check its Text to Speech permission."),
        HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden => Fail(DialogProviderFailure.AccountRestriction, "ElevenLabs denied this voice or account. Check subscription and voice access."),
        HttpStatusCode.TooManyRequests => Fail(DialogProviderFailure.RateLimited, "ElevenLabs is rate limiting requests. Wait before trying again."),
        HttpStatusCode.RequestTimeout => Fail(DialogProviderFailure.Timeout, "ElevenLabs timed out processing the request."),
        >= HttpStatusCode.InternalServerError => Fail(DialogProviderFailure.Network, "The ElevenLabs service is unavailable. Try again later."),
        _ => Fail(DialogProviderFailure.InvalidRequest, $"ElevenLabs generation failed (HTTP {(int)status}).")
    };

    private static DialogProviderException StreamingFailure(JsonElement error)
    {
        // Allowlisted codes only. Provider text can contain request content or credentials.
        var code = error.ValueKind == JsonValueKind.String ? error.GetString()
            : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        return code switch
        {
            "invalid_api_key" or "not_authenticated" or "authentication_required" => HttpFailure(HttpStatusCode.Unauthorized),
            "payment_required" or "quota_exceeded" => HttpFailure(HttpStatusCode.PaymentRequired),
            "rate_limit_exceeded" or "too_many_concurrent_requests" => HttpFailure(HttpStatusCode.TooManyRequests),
            _ => Fail(DialogProviderFailure.Protocol, "ElevenLabs rejected the streaming request. Check model, voice and account access.")
        };
    }

    private static DialogProviderException Fail(DialogProviderFailure reason, string message) => new(reason, message);
    private static async Task ObserveCleanupAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception)
        {
            // Observe any later failure without retaining the socket or blocking the caller.
            _ = task.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
    public void Dispose() => _http.Dispose();
}
