using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    /// <summary>
    /// Loads the radar overlay's own UI text - stat labels in EnemyControl, option labels in
    /// OptionsControl - from Locales/{locale}.txt: one "Key=Value" text file per locale, shipped
    /// next to the exe. This is separate from Configuration.Locale's other job of picking which
    /// of the game's own "lang/" folders to read creature/item names from - the same locale
    /// selection just drives both, and this one degrades to English whenever a matching radar
    /// translation file hasn't been added yet.
    /// </summary>
    public static class RadarLocalization
    {
        private const string DefaultLocale = "en_US";
        private static string customLocaleFolder;
        private static string[] customLocales = Array.Empty<string>();

        // When an updated binary runs from the cache, keep loose user translations in
        // the original installation, but load newly shipped defaults from its own release.
        public static string FindCustomLocales(string installFolder)
        {
            var folder = Path.Combine(installFolder, "Locales");
            if (!Directory.Exists(folder)) return "";
            var assembly = typeof(RadarLocalization).Assembly;
            var changed = new List<string>();
            foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                using (var baseline = assembly.GetManifestResourceStream($"BGOverlay.Locales.{name}.txt"))
                {
                    if (baseline != null)
                    {
                        using (var reader = new StreamReader(baseline))
                            if (File.ReadAllText(file) == reader.ReadToEnd()) continue;
                    }
                    changed.Add(name);
                }
            }
            return string.Join("|", changed);
        }

        public static void UseCustomLocales(string installFolder, string names)
        {
            customLocaleFolder = Path.Combine(installFolder, "Locales");
            customLocales = (names ?? "").Split('|');
        }

        public static Dictionary<string, string> Strings { get; private set; } = new Dictionary<string, string>();

        /// <summary>
        /// A localized string by key, or the key itself if it's missing (e.g. Init() was never
        /// called, or somehow even the English baseline couldn't be loaded) - visibly wrong
        /// rather than silently blank.
        /// </summary>
        public static string Get(string key)
        {
            return Strings.TryGetValue(key, out var value) ? value : key;
        }

        internal static string FormatArmorClassModifier(int type, int amount)
        {
            // Existing translations include a literal plus before the placeholder.
            string Bonus(string key) => string.Format(Get(key).Replace("+{0}", "{0:+0;-0;0}"), amount);
            if (type == 0) return Bonus("Str_ACBonus");
            if (type == 16) return string.Format(Get("Str_SetACTo"), amount);
            if ((type & ~15) != 0) return null;

            var bonuses = new List<string>();
            if ((type & 1) != 0) bonuses.Add(Bonus("Str_ACvsCrushing"));
            if ((type & 2) != 0) bonuses.Add(Bonus("Str_ACvsMissile"));
            if ((type & 4) != 0) bonuses.Add(Bonus("Str_ACvsPiercing"));
            if ((type & 8) != 0) bonuses.Add(Bonus("Str_ACvsSlashing"));
            return string.Join(", ", bonuses);
        }

        /// <summary>
        /// Like <see cref="Get"/>, but reports whether the key was actually found instead of
        /// silently falling back to the key itself - for callers (e.g. Effect/Proficiency enum
        /// name lookups in BGEntity) that have their own, more useful fallback (a readable
        /// English-shaped name derived from the enum member) to use when no translation exists
        /// yet for that particular value.
        /// </summary>
        public static bool TryGet(string key, out string value)
        {
            return Strings.TryGetValue(key, out value);
        }

        public static void Init(string locale)
        {
            Logger.Debug($"RadarLocalization.Init: requested locale = '{locale}', app directory = '{getAppDirectory()}'");

            // Load English as the baseline first and let the selected locale override on top
            // of it, so a translation file that's missing a key (e.g. one added after the file
            // was last updated) falls back to English for that key instead of showing nothing.
            var result = new Dictionary<string, string>();
            loadLocale(DefaultLocale, result);
            if (!string.IsNullOrEmpty(locale) && !string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
                loadLocale(locale, result);
            Strings = result;

            Logger.Debug($"RadarLocalization.Init: loaded {Strings.Count} string(s)");
        }

        /// <summary>
        /// The directory the actual .exe lives in. Not AppDomain.CurrentDomain.BaseDirectory /
        /// AppContext.BaseDirectory - for a self-contained single-file publish those resolve to
        /// the .NET single-file host's temp extraction cache (%TEMP%\.net\...), not the folder
        /// the user actually published/ran the exe from, so a "Locales" folder shipped next to
        /// the exe would never be found there. Process.MainModule.FileName is the same API
        /// Configuration.Init already uses to locate the game's own executable, and it correctly
        /// resolves to the running apphost's real path for a single-file exe too.
        /// </summary>
        private static string getAppDirectory()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    return Path.GetDirectoryName(exePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Could not resolve the running executable's own directory!", ex);
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static void loadLocale(string locale, Dictionary<string, string> result)
        {
            var assembly = typeof(RadarLocalization).Assembly;
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(name =>
                string.Equals(name, $"BGOverlay.Locales.{locale}.txt", StringComparison.OrdinalIgnoreCase));
            if (resource != null)
            {
                using (var reader = new StreamReader(assembly.GetManifestResourceStream(resource)))
                    readStrings(reader, result);
            }

            // Loose translations override the built-in defaults, including individual edits.
            var folder = customLocales.Contains(locale, StringComparer.OrdinalIgnoreCase)
                ? customLocaleFolder : Path.Combine(getAppDirectory(), "Locales");
            try
            {
                if (!Directory.Exists(folder)) return;
                var file = Directory.EnumerateFiles(folder, "*.txt").FirstOrDefault(path =>
                    string.Equals(Path.GetFileNameWithoutExtension(path), locale, StringComparison.OrdinalIgnoreCase));
                if (file == null) return;
                using (var reader = File.OpenText(file))
                    readStrings(reader, result);
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not read the external {locale} translation from '{folder}'", ex);
            }
        }

        private static void readStrings(TextReader reader, Dictionary<string, string> result)
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                    continue;
                int separator = line.IndexOf('=');
                if (separator > 0)
                    result[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
        }
    }
}
