namespace WhatsAppSalesAutomation.Application.Razorpay;

/// <summary>One mode's saved keys. Empty strings when nothing is stored.</summary>
public record RazorpayKeySet(string Mode, string KeyId, string KeySecret, string WebhookSecret)
{
    /// <summary>Key id and key secret: enough to call Razorpay.</summary>
    public bool IsConfigured => KeyId.Length > 0 && KeySecret.Length > 0;

    /// <summary>Key id, key secret and webhook secret: enough to take payments and confirm them.</summary>
    public bool IsComplete => IsConfigured && WebhookSecret.Length > 0;

    public RazorpayCredentials? Credentials => IsConfigured ? new RazorpayCredentials(KeyId, KeySecret) : null;
}

/// <summary>
/// The platform keeps two Razorpay key sets side by side - Test (rzp_test_...) and Live (rzp_live_...) - and an operator-chosen active mode.
/// New orders, checkouts and mandates use the ACTIVE set; anything already created (an order, a top-up, a mandate) keeps using the set of the mode
/// it was created in, so switching modes never strands an in-flight payment. Every reader of the Razorpay keys goes through here.
/// <para>Before both sets existed there was one key id/secret/webhook secret (Razorpay:KeyId ...). Those are still read - as the slot their key
/// id's prefix names, and as the active mode when none was chosen - until the first save on the settings page moves them into their slot.</para>
/// </summary>
public static class RazorpayKeyStore
{
    public const string Test = "test";
    public const string Live = "live";
    public static readonly IReadOnlyList<string> Modes = new[] { Test, Live };

    public const string ActiveModeKey = "Razorpay:ActiveMode";

    // The single key set from before Test and Live were kept apart.
    public const string LegacyKeyIdKey = "Razorpay:KeyId";
    public const string LegacyKeySecretKey = "Razorpay:KeySecret";
    public const string LegacyWebhookSecretKey = "Razorpay:WebhookSecret";

    public static string KeyIdKey(string mode) => $"Razorpay:{Slot(mode)}:KeyId";
    public static string KeySecretKey(string mode) => $"Razorpay:{Slot(mode)}:KeySecret";
    public static string WebhookSecretKey(string mode) => $"Razorpay:{Slot(mode)}:WebhookSecret";

    /// <summary>"test" or "live" for anything that names one (any case), otherwise null.</summary>
    public static string? Normalize(string? mode) =>
        string.Equals(mode?.Trim(), Test, StringComparison.OrdinalIgnoreCase) ? Test
        : string.Equals(mode?.Trim(), Live, StringComparison.OrdinalIgnoreCase) ? Live
        : null;

    public static string Other(string mode) => Normalize(mode) == Live ? Test : Live;

    /// <summary>The operator's choice; before one was made, the mode of the old single key id; otherwise Test.</summary>
    public static string ActiveMode(IReadOnlyDictionary<string, string?> stored)
    {
        var chosen = Normalize(Get(stored, ActiveModeKey));
        if (chosen is not null)
            return chosen;
        var legacy = Get(stored, LegacyKeyIdKey);
        return legacy.Length > 0 ? RazorpayKeys.ModeOf(legacy) : Test;
    }

    public static RazorpayKeySet Active(IReadOnlyDictionary<string, string?> stored) => For(stored, ActiveMode(stored));

    public static RazorpayKeySet For(IReadOnlyDictionary<string, string?> stored, string mode)
    {
        var m = Normalize(mode) ?? Test;
        var keyId = Get(stored, KeyIdKey(m));
        var secret = Get(stored, KeySecretKey(m));
        var webhook = Get(stored, WebhookSecretKey(m));
        if (keyId.Length == 0 && secret.Length == 0 && webhook.Length == 0)
        {
            var legacyId = Get(stored, LegacyKeyIdKey);
            if (legacyId.Length > 0 && RazorpayKeys.ModeOf(legacyId) == m)
                return new RazorpayKeySet(m, legacyId, Get(stored, LegacyKeySecretKey), Get(stored, LegacyWebhookSecretKey));
        }
        return new RazorpayKeySet(m, keyId, secret, webhook);
    }

    /// <summary>Credentials for a record created in <paramref name="mode"/> - an order, top-up or mandate - or null when that mode has no keys.</summary>
    public static RazorpayCredentials? CredentialsFor(IReadOnlyDictionary<string, string?> stored, string? mode) =>
        For(stored, Normalize(mode) ?? ActiveMode(stored)).Credentials;

    /// <summary>The webhook secrets to check a delivery against: the active mode's first, then the other's. Razorpay sends Test and Live webhooks
    /// to the same URL, each signed with the secret set on that mode's webhook.</summary>
    public static IReadOnlyList<(string Mode, string Secret)> WebhookSecrets(IReadOnlyDictionary<string, string?> stored)
    {
        var active = ActiveMode(stored);
        return new[] { active, Other(active) }
            .Select(m => (Mode: m, Secret: For(stored, m).WebhookSecret))
            .Where(x => x.Secret.Length > 0)
            .ToList();
    }

    /// <summary>The writes that move the old single key set into its slot and pin the active mode, or nothing once that has been done. Saved along
    /// with the first change on the settings page.</summary>
    public static Dictionary<string, string?> LegacyMigration(IReadOnlyDictionary<string, string?> stored)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var legacyId = Get(stored, LegacyKeyIdKey);
        if (Normalize(Get(stored, ActiveModeKey)) is null)
            values[ActiveModeKey] = ActiveMode(stored);
        if (legacyId.Length == 0)
            return values;

        var mode = RazorpayKeys.ModeOf(legacyId);
        var slot = For(stored, mode);
        values[KeyIdKey(mode)] = slot.KeyId;
        values[KeySecretKey(mode)] = slot.KeySecret;
        values[WebhookSecretKey(mode)] = slot.WebhookSecret;
        values[LegacyKeyIdKey] = string.Empty;
        values[LegacyKeySecretKey] = string.Empty;
        values[LegacyWebhookSecretKey] = string.Empty;
        return values;
    }

    private static string Slot(string mode) => Normalize(mode) == Live ? "Live" : "Test";

    private static string Get(IReadOnlyDictionary<string, string?> stored, string key) =>
        stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;
}
