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
            var result = loadFile(DefaultLocale) ?? new Dictionary<string, string>();
            if (!string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
            {
                var overrides = loadFile(locale);
                if (overrides != null)
                {
                    foreach (var entry in overrides)
                        result[entry.Key] = entry.Value;
                }
            }
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

        private static Dictionary<string, string> loadFile(string locale)
        {
            if (string.IsNullOrEmpty(locale))
                return null;

            var localesDir = Path.Combine(getAppDirectory(), "Locales");
            if (!Directory.Exists(localesDir))
            {
                // Logger.Error() is a no-op without an exception (see Logger.cs) - use Info so
                // this actually reaches radar.log.
                Logger.Info($"RadarLocalization: Locales folder not found at '{localesDir}'");
                return null;
            }

            // Configuration.Locale is normalized to lowercase (Configuration.getProperty lowercases
            // everything it reads from config.cfg), while locale files are named to match the
            // game's own lang/ folder casing (e.g. "en_US.txt", "ru_RU.txt"). Match case-insensitively
            // by hand instead of relying on the filesystem to do it - that's not guaranteed on every
            // setup (e.g. a case-sensitive NTFS directory, or however a single-file publish's
            // self-extract cache behaves).
            var candidates = Directory.EnumerateFiles(localesDir, "*.txt").ToList();
            var path = candidates.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), locale, StringComparison.OrdinalIgnoreCase));
            if (path == null)
            {
                Logger.Info($"RadarLocalization: no locale file matching '{locale}' in '{localesDir}'. Found: {string.Join(", ", candidates.Select(Path.GetFileName))}");
                return null;
            }

            try
            {
                var result = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
                        continue;

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    var key = line.Substring(0, separatorIndex).Trim();
                    var value = line.Substring(separatorIndex + 1).Trim();
                    result[key] = value;
                }
                Logger.Debug($"RadarLocalization: loaded {result.Count} string(s) from '{path}'");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not load locale file: {path}", ex);
                return null;
            }
        }
    }
}
