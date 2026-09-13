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
    }

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

        if (transactionId.Length == 0 || amount <= 0)
            return null;

        return new Verified { TransactionId = transactionId, Sku = sku, Amount = amount, UserId = userId };
    }
}
