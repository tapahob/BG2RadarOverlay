using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// Verifies an HS256-signed Twitch Extension JWT and hands back its decoded payload - shared by
/// everything that has to trust a token signed with the extension's shared secret: a Bits
/// purchase receipt (BitsReceipt.cs) and a viewer's own onAuthorized identity token (used to
/// prove who is spending tokens in /api/summon and who is asking in /api/balance).
///
/// Twitch hands the signing secret out base64-encoded (Dev Console -> Extensions -> Manage ->
/// Secret) - callers decode it once into raw bytes, not on every verification.
/// </summary>
public static class Jwt
{
    /// <summary>
    /// Null for anything that doesn't check out - bad signature, expired, malformed. Callers must
    /// treat null as "this token proves nothing", not guess at a reason: a forged/tampered token
    /// can make any individual field claim whatever it wants, so nothing in it is safe to read
    /// before the signature check passes.
    /// </summary>
    public static JsonElement? TryVerifyAndDecode(string jwt, byte[] secret)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return null;

        byte[] expectedSig;
        try
        {
            expectedSig = base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        byte[] actualSig;
        using (var hmac = new HMACSHA256(secret))
            actualSig = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));

        // Constant-time: a timing difference between "wrong at byte 2" and "wrong at byte 30" is
        // exactly the kind of side channel that turns into a forgery oracle.
        if (expectedSig.Length != actualSig.Length || !CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
            return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(base64UrlDecode(parts[1]));
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // `exp` is *required*, not just honoured when present. Every token this relay accepts
            // (onAuthorized, Bits receipt) is issued by Twitch and always carries one, and a
            // signed token with no expiry would be valid forever - which is exactly what turns a
            // receipt a viewer's browser still holds into an unlimited free top-up once the
            // replay guard's entry for it has aged out.
            if (!root.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out var expUnix))
                return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix + clockSkewSeconds)
                return null;

            // Clone: doc is disposed at the end of this block, and a JsonElement isn't valid to
            // read from a disposed document - Clone makes an independent copy the caller can keep
            // using afterward.
            return root.Clone();
        }
    }

    /// <summary>
    /// The `exp` of an already-verified payload, as UTC - what the replay guard files a spent
    /// Bits transaction id under, so a receipt's dedup entry always outlives the receipt itself.
    /// </summary>
    public static DateTime? TryGetExpiry(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("exp", out var expEl)
            || !expEl.TryGetInt64(out var expUnix))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
    }

    // A minute of tolerance for clock drift between this VPS and Twitch - small enough that it
    // never meaningfully extends a token's life, large enough that a slightly fast clock here
    // doesn't reject a receipt a viewer just paid real Bits for.
    private const long clockSkewSeconds = 60;

    private static byte[] base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
