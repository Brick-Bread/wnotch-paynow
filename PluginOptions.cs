using System.Security.Cryptography;
using System.Text;
using Notch.Core.Plugins;

namespace PayNowAlerts;

/// <summary>
/// The plugin's options, read from its settings.json. They are listed in plugin.json, so Notch shows
/// them in its Settings window and restarts the plugin when they change.
/// </summary>
internal sealed record PluginOptions
{
    private const string ApiKeyKey = "apiKey";
    private const string ApiKeyProtectedKey = "apiKeyProtected";

    public string StoreId { get; init; } = "";

    public string? ApiKey { get; init; }

    /// <summary>What the user has to fix before the plugin can run; null when it can.</summary>
    public string? Problem { get; init; }

    public string Animation { get; init; } = "Confetti";

    /// <summary>In the store's smallest currency unit; 0 for no big sales.</summary>
    public long BigSaleMinor { get; init; } = 2000;

    public bool KeepAlert { get; init; } = true;

    public bool SkipFree { get; init; }

    public int PollSeconds { get; init; } = 20;

    public static PluginOptions Load(IPluginSettings settings, IPluginLog log)
    {
        var defaults = new PluginOptions();

        string storeId = settings.Get("storeId", "").Trim();
        string? apiKey = ReadApiKey(settings, log, out bool unreadable);
        double bigSale = Math.Clamp(settings.Get("bigSale", 20.0), 0, 100000);

        var options = new PluginOptions
        {
            StoreId = storeId,
            ApiKey = apiKey,
            Problem = storeId.Length == 0 ? "Enter your store ID and API key in Notch Settings."
                : !storeId.All(char.IsAsciiDigit) ? "The store ID is a number, like 613881873059291136. Check it in Notch Settings."
                : unreadable ? "The stored API key cannot be read here. Enter it again in Notch Settings."
                : apiKey is null ? "Enter your PayNow API key in Notch Settings."
                : null,
            Animation = settings.Get("animation", defaults.Animation) ?? defaults.Animation,
            BigSaleMinor = (long)Math.Round(bigSale * 100),
            KeepAlert = settings.Get("keepAlert", defaults.KeepAlert),
            SkipFree = settings.Get("skipFree", defaults.SkipFree),
            PollSeconds = Math.Clamp(settings.Get("pollSeconds", defaults.PollSeconds), 10, 600),
        };

        // Written back so every option shows up in settings.json with its value.
        settings.Set("storeId", storeId);
        settings.Set("animation", options.Animation);
        settings.Set("bigSale", bigSale);
        settings.Set("keepAlert", options.KeepAlert);
        settings.Set("skipFree", options.SkipFree);
        settings.Set("pollSeconds", options.PollSeconds);
        return options;
    }

    /// <summary>
    /// Takes a key typed into Settings, encrypts it for this Windows account and clears the plain
    /// copy, so settings.json never holds the key in the clear for longer than one start.
    /// </summary>
    private static string? ReadApiKey(IPluginSettings settings, IPluginLog log, out bool unreadable)
    {
        unreadable = false;

        string typed = settings.Get(ApiKeyKey, "").Trim();
        if (typed.Length > 0)
        {
            settings.Set(ApiKeyProtectedKey, Protect(typed));
            settings.Set(ApiKeyKey, "");
            log.Info("Encrypted the API key from settings.json.");
            return typed;
        }

        string stored = settings.Get(ApiKeyProtectedKey, "");
        if (stored.Length == 0)
        {
            return null;
        }

        try
        {
            return Unprotect(stored);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            unreadable = true;
            log.Warn("The stored API key could not be decrypted; it was probably saved by another Windows account.");
            return null;
        }
    }

    private static readonly byte[] Entropy = "brick-bread.paynow"u8.ToArray();

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
}
