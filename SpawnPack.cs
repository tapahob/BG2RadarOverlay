using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BGOverlay
{
    /// <summary>
    /// One creature entry within a <see cref="SpawnPack"/>: what to summon and how many.
    /// </summary>
    public sealed class SpawnEntry
    {
        public string ResRef { get; set; }
        public int Amount { get; set; }

        public override string ToString()
        {
            return $"{ResRef} x{Amount}";
        }
    }

    /// <summary>
    /// A set of creatures to summon for a given character-level band, so a viewer redeeming a
    /// summon at level 1 gets gibberlings rather than something that would flatten the party.
    ///
    /// Packs are persisted into the flat key=value config.cfg as a single line, since that file
    /// has no support for nesting:
    ///
    ///     SpawnPacks=1-3|gibber:3,xvart:2;4-6|ogre:1
    ///
    /// Lowercase throughout, because Configuration.getProperty() lowercases every value it
    /// reads back; ResRefs are upper-cased again before being sent to the game.
    /// </summary>
    public sealed class SpawnPack
    {
        public int LevelFrom { get; set; }
        public int LevelTo { get; set; }
        public List<SpawnEntry> Entries { get; set; } = new List<SpawnEntry>();

        public bool Matches(int level)
        {
            return level >= LevelFrom && level <= LevelTo;
        }

        public override string ToString()
        {
            var entries = Entries.Count > 0
                ? string.Join(", ", Entries.Select(e => e.ToString()).ToArray())
                : "(empty)";
            return $"Lv {LevelFrom}-{LevelTo}: {entries}";
        }

        public static string Serialize(IEnumerable<SpawnPack> packs)
        {
            var parts = packs
                .Where(p => p.Entries.Count > 0)
                .Select(p => $"{p.LevelFrom}-{p.LevelTo}|" +
                             string.Join(",", p.Entries.Select(e => $"{e.ResRef.ToLowerInvariant()}:{e.Amount}").ToArray()));
            return string.Join(";", parts.ToArray());
        }

        /// <summary>
        /// Never throws: a malformed or hand-edited config should cost the user their packs, not
        /// crash the overlay on startup, so anything unparseable is simply skipped.
        /// </summary>
        public static List<SpawnPack> Deserialize(string value)
        {
            var packs = new List<SpawnPack>();
            if (string.IsNullOrWhiteSpace(value))
                return packs;

            foreach (var packText in value.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(packText))
                    continue;

                var halves = packText.Split('|');
                if (halves.Length != 2)
                    continue;

                var range = halves[0].Split('-');
                if (range.Length != 2)
                    continue;

                if (!int.TryParse(range[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                    continue;
                if (!int.TryParse(range[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
                    continue;

                var pack = new SpawnPack { LevelFrom = from, LevelTo = to };

                foreach (var entryText in halves[1].Split(','))
                {
                    if (string.IsNullOrWhiteSpace(entryText))
                        continue;

                    var entryParts = entryText.Split(':');
                    if (entryParts.Length != 2)
                        continue;

                    var resref = entryParts[0].Trim();
                    if (resref.Length == 0)
                        continue;

                    if (!int.TryParse(entryParts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
                        continue;

                    pack.Entries.Add(new SpawnEntry { ResRef = resref, Amount = amount });
                }

                if (pack.Entries.Count > 0)
                    packs.Add(pack);
            }

            return packs;
        }

        /// <summary>
        /// The pack covering <paramref name="level"/>, or null if none does. Narrower bands win,
        /// so a specific "5-5" pack overrides a catch-all "1-40" without the user having to
        /// worry about the order they defined them in.
        /// </summary>
        public static SpawnPack ForLevel(IEnumerable<SpawnPack> packs, int level)
        {
            SpawnPack best = null;
            foreach (var pack in packs)
            {
                if (!pack.Matches(level))
                    continue;
                if (best == null || (pack.LevelTo - pack.LevelFrom) < (best.LevelTo - best.LevelFrom))
                    best = pack;
            }
            return best;
        }

        /// <summary>
        /// Parses the editor's free-text entry list - one "resref amount" or "resref:amount" per
        /// line - returning what it understood plus whatever it couldn't, so the UI can tell the
        /// user which lines were ignored instead of silently dropping them.
        /// </summary>
        public static List<SpawnEntry> ParseEntryLines(string text, out List<string> rejected)
        {
            var entries = new List<SpawnEntry>();
            rejected = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
                return entries;

            var separators = new[] { '\r', '\n' };
            foreach (var rawLine in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                // Split on separators only - never on 'x'. Supporting the "GIBBER x3" form by
                // splitting on the letter silently mangles any ResRef containing one (XVART
                // became VART), so the 'x' is stripped from the count instead.
                var parts = line.Split(new[] { ':', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var resref = parts.Length > 0 ? parts[0].Trim() : "";
                var amount = 1;

                if (parts.Length > 1)
                {
                    var countText = parts[parts.Length - 1].Trim();
                    if (countText.StartsWith("x", StringComparison.OrdinalIgnoreCase))
                        countText = countText.Substring(1);

                    if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
                    {
                        rejected.Add(line);
                        continue;
                    }
                }

                if (!IsValidResRef(resref) || amount < 1 || amount > GameSpawnBridge.MaxAmount)
                {
                    rejected.Add(line);
                    continue;
                }

                entries.Add(new SpawnEntry { ResRef = resref.ToUpperInvariant(), Amount = amount });
            }

            return entries;
        }

        public static string ToEntryLines(IEnumerable<SpawnEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
                sb.AppendLine($"{entry.ResRef.ToUpperInvariant()} {entry.Amount}");
            return sb.ToString().TrimEnd();
        }

        public static bool IsValidResRef(string resref)
        {
            if (string.IsNullOrEmpty(resref) || resref.Length > 8)
                return false;

            foreach (var c in resref)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '_';
                if (!ok)
                    return false;
            }
            return true;
        }
    }
}
