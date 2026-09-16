using System.Text.Json;

namespace TwitchRelay;

/// <summary>
/// The bits-transaction-receipt-specific fields out of a JWT that Jwt.cs has already verified -
/// the payload Twitch.ext.bits.onTransactionComplete hands the extension frontend when a Bits
/// purchase completes, which the frontend then posts to us as proof of payment. Without the
/// signature check (done in Jwt.TryVerifyAndDecode), "I paid" would just be whatever a viewer's
/// browser claims - exactly the kind of thing the rest of this project treats as untrusted client
/// input (see GameSpawnBridge's own sanitizing, or the relay's command normalizer).
/// </summary>
public static class BitsReceipt
{
    public sealed class Verified
    {
        public string TransactionId { get; init; } = "";
        public string Sku { get; init; } = "";
        public int Amount { get; init; }
        public string UserId { get; init; } = "";
        /// <summary>
        /// When this receipt stops being accepted at all - the replay guard keeps its spent
        /// transaction id at least this long, so there is never a window where a receipt still
        /// verifies but the record of it having already been cashed in has been pruned.
        /// </summary>
        public DateTime ExpiresAtUtc { get; init; }
        /// <summary>
        /// Whose channel the purchase was made on, when Twitch includes it - empty if this
        /// receipt shape doesn't carry one, which is why /api/bits-purchase binds the purchase to
        /// a channel via the viewer's own onAuthorized token as well and doesn't rely on this
        /// alone. Never trust a receipt to be for *this* streamer just because it verifies: the
        /// extension's signing secret is one value shared by every channel the extension runs on.
        /// </summary>
        public string ChannelId { get; init; } = "";
    }

    private const int maxBitsPerTransaction = 10_000;

    public static Verified? TryVerify(string jwt, byte[] secret)
    {
        var payload = Jwt.TryVerifyAndDecode(jwt, secret);
        if (payload is null)
            return null;
        var root = payload.Value;

        if (!root.TryGetProperty("topic", out var topicEl) || topicEl.GetString() != "bits_transaction_receipt")
            return null;
        if (!root.TryGetProperty("data", out var data))
            return null;

        var transactionId = data.TryGetProperty("transactionId", out var tid) ? tid.GetString() ?? "" : "";
        var userId = data.TryGetProperty("userId", out var uid) ? uid.GetString() ?? "" : "";
        var sku = "";
        var amount = 0;

        if (data.TryGetProperty("product", out var product))
        {
            sku = product.TryGetProperty("sku", out var skuEl) ? skuEl.GetString() ?? "" : "";
            if (product.TryGetProperty("cost", out var cost) && cost.TryGetProperty("amount", out var amountEl))
            {
                // Twitch's own reference example shows this as a string ("10"), not a JSON
                // number - accept whichever kind actually arrives rather than assume one.
                if (amountEl.ValueKind == JsonValueKind.Number)
                    amountEl.TryGetInt32(out amount);
                else if (amountEl.ValueKind == JsonValueKind.String)
                    int.TryParse(amountEl.GetString(), out amount);
            }
        }

        // Twitch caps a single Bits transaction at 10,000; anything beyond that is not a
        // purchase shape Twitch can actually produce, and an unbounded amount summed across a
        // batch of receipts is an integer-overflow lever besides.
        if (transactionId.Length == 0 || amount <= 0 || amount > maxBitsPerTransaction)
            return null;

        var expiresAt = Jwt.TryGetExpiry(root);
        if (expiresAt is null)
            return null;

        var channelId = "";
        if (root.TryGetProperty("channel_id", out var chanEl) && chanEl.ValueKind == JsonValueKind.String)
            channelId = chanEl.GetString() ?? "";
        else if (data.TryGetProperty("channelId", out var chanEl2) && chanEl2.ValueKind == JsonValueKind.String)
            channelId = chanEl2.GetString() ?? "";

        return new Verified
        {
            TransactionId = transactionId,
            Sku = sku,
            Amount = amount,
            UserId = userId,
            ExpiresAtUtc = expiresAt.Value,
            ChannelId = channelId
        };
    }
}
