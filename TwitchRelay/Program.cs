using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

// Relay between a streamer's overlay and their viewers.
//
// Upstream (overlay -> relay): party snapshots, keyed by a "stream key". Only the latest
// snapshot per key is kept, in memory; a restart forgets everyone until their overlay
// reconnects and pushes again.
//
// Downstream (relay -> overlay): commands, e.g. a viewer-triggered summon. These travel back
// down the same WebSocket and are addressed by a *separate* control key, never the stream key
// - the stream key ends up in the Twitch Extension's broadcaster config, which Twitch serves
// to every viewer's browser, so it is effectively public and must not authorize anything that
// touches the streamer's game.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    // The read endpoint is polled by a browser-hosted viewer frontend from an origin this
    // relay can't predict, and it only returns what the streamer is already broadcasting -
    // same trust model as the stream itself.
    //
    // GET only, so no page can read a command endpoint's response. Note what that does NOT do:
    // CORS never stops a cross-origin POST from being *delivered* - any page can send one as a
    // simple text/plain request and the endpoint still runs, it just can't see the reply. The
    // only thing protecting the game is the control key being secret, which is why it never goes
    // into the extension configuration Twitch serves to viewers. (The local mock does POST this
    // way on purpose, with a key the streamer pasted in themselves.)
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().WithMethods("GET"));
});

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

var snapshots = new ConcurrentDictionary<string, (string Json, DateTime UpdatedUtc)>();
var commandWriters = new ConcurrentDictionary<string, ChannelWriter<string>>();
var keyPattern = new Regex("^[a-z0-9]{8,64}$");
var staleAfter = TimeSpan.FromSeconds(15);

app.MapGet("/health", () => Results.Text("OK"));

app.Map("/ws/ingest/{streamKey}", async (HttpContext context, string streamKey) =>
{
    if (!keyPattern.IsMatch(streamKey) || !context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var connectionClosed = new CancellationTokenSource();

    // Bounded so a wedged or slow overlay can't grow this without limit; dropping the oldest
    // matters more than delivering every summon, since a stale backlog would fire minutes
    // after the viewer redeemed it.
    var commands = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    string? controlKey = null;

    // One writer task: WebSocket.SendAsync must not be called concurrently.
    var sendLoop = Task.Run(async () =>
    {
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync(connectionClosed.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(command);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, connectionClosed.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    });

    var buffer = new byte[8192];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            var json = Encoding.UTF8.GetString(message.ToArray());

            // The overlay's first message is a handshake carrying its control key. Only worth
            // parsing until that arrives - everything after it is a snapshot, stored verbatim.
            if (controlKey is null)
            {
                if (tryReadControlKey(json, out var declared) && keyPattern.IsMatch(declared))
                {
                    controlKey = declared;
                    commandWriters[controlKey] = commands.Writer;
                    continue;
                }
            }

            snapshots[streamKey] = (json, DateTime.UtcNow);
        }
    }
    catch (WebSocketException)
    {
        // Client vanished mid-read; fall through to cleanup.
    }
    finally
    {
        connectionClosed.Cancel();
        commands.Writer.TryComplete();
        if (controlKey is not null)
            commandWriters.TryRemove(controlKey, out _);
        await sendLoop;
    }
});

app.MapGet("/api/character/{streamKey}", (string streamKey) =>
{
    if (!snapshots.TryGetValue(streamKey, out var entry))
        return Results.NotFound();

    if (DateTime.UtcNow - entry.UpdatedUtc > staleAfter)
        return Results.NotFound();

    return Results.Content(entry.Json, "application/json");
});

// Sends a command down to a connected overlay. Addressed by control key, which only the
// overlay and this relay ever see - see the note at the top about why the stream key can't
// be used here. Eventually the caller is our own Twitch EventSub handler rather than anything
// public.
app.MapPost("/api/command/{controlKey}", async (string controlKey, HttpContext context) =>
{
    if (!keyPattern.IsMatch(controlKey))
        return Results.BadRequest();

    if (!commandWriters.TryGetValue(controlKey, out var writer))
        return Results.NotFound();

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
        return Results.BadRequest();

    // Rebuilt rather than forwarded verbatim. The `message` field carries whatever a viewer
    // typed, and it ends up in the streamer's game, so this is the point where it stops being
    // arbitrary: unknown fields are dropped, and the text is cut to a shape the downstream
    // hand-rolled parser and the engine's message log can both take.
    if (!tryNormalizeCommand(body, out var command))
        return Results.BadRequest();

    return writer.TryWrite(command) ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.Run();

// Kept in step with GameSpawnBridge.MaxMessageLength on the overlay side, which cuts again at
// the mailbox. Both ends clamp: this relay is the only thing between a viewer and the game, but
// a stale or third-party relay must not be able to overrun the buffer either.
const int maxMessageLength = 95;

// A viewer can only tick as many tiles as the streamer has packs, so this is a backstop against
// a forged command rather than a limit anyone should meet.
const int maxPacksPerSummon = 8;

static bool tryNormalizeCommand(string body, out string command)
{
    command = "";
    try
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != "summon")
            return false;

        string? resref = null;
        if (root.TryGetProperty("resref", out var resrefElement)
            && resrefElement.ValueKind == JsonValueKind.String)
        {
            var candidate = resrefElement.GetString() ?? "";
            if (candidate.Length is > 0 and <= 8 && candidate.All(c => char.IsLetterOrDigit(c) || c == '_'))
                resref = candidate;
        }

        var amount = 1;
        if (root.TryGetProperty("amount", out var amountElement)
            && amountElement.ValueKind == JsonValueKind.Number
            && amountElement.TryGetInt32(out var parsedAmount))
        {
            amount = Math.Clamp(parsedAmount, 1, 20);
        }

        // Ids of the packs the viewer picked, in the order sent. Capped: the overlay spawns
        // every pack named here, so an unbounded list is an unbounded spawn.
        var packs = new List<string>();
        if (root.TryGetProperty("packs", out var packsElement)
            && packsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in packsElement.EnumerateArray())
            {
                if (packs.Count >= maxPacksPerSummon)
                    break;
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var id = item.GetString() ?? "";
                if (id.Length is > 0 and <= 32
                    && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                    && !packs.Contains(id))
                {
                    packs.Add(id);
                }
            }
        }

        var message = "";
        if (root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String)
        {
            message = sanitizeMessage(messageElement.GetString() ?? "");
        }

        var payload = new Dictionary<string, object> { ["type"] = "summon" };
        if (resref is not null)
        {
            payload["resref"] = resref;
            payload["amount"] = amount;
        }
        if (packs.Count > 0)
            payload["packs"] = packs;
        if (message.Length > 0)
            payload["message"] = message;

        command = JsonSerializer.Serialize(payload);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

// Printable ASCII only, whitespace collapsed, length-capped. Quotes and backslashes go too:
// the overlay reads this back with a hand-rolled string scanner rather than a JSON parser, and
// text that can close its own field there could make one command look like another.
static string sanitizeMessage(string raw)
{
    var builder = new StringBuilder(maxMessageLength);
    var lastWasSpace = true;

    foreach (var c in raw)
    {
        if (c is ' ' or '\t' or '\r' or '\n')
        {
            if (!lastWasSpace && builder.Length < maxMessageLength)
                builder.Append(' ');
            lastWasSpace = true;
            continue;
        }

        if (c < 32 || c > 126 || c is '"' or '\\')
            continue;

        if (builder.Length >= maxMessageLength)
            break;

        builder.Append(c);
        lastWasSpace = false;
    }

    return builder.ToString().TrimEnd();
}

static bool tryReadControlKey(string json, out string controlKey)
{
    controlKey = "";
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        if (!document.RootElement.TryGetProperty("control", out var element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        controlKey = element.GetString() ?? "";
        return controlKey.Length > 0;
    }
    catch (JsonException)
    {
        return false;
    }
}
