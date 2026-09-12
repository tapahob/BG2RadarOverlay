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
    ///     SpawnPacks=gibberlings|1-3|gibber:3,xvart:2;ogres|4-6|ogre:1
    ///
    /// Lowercase throughout, because Configuration.getProperty() lowercases every value it
    /// reads back; ResRefs are upper-cased again before being sent to the game.
    /// </summary>
    public sealed class SpawnPack
    {
        /// <summary>
        /// What viewers see on the pack's tile in the Twitch extension. Optional - an unnamed
        /// pack falls back to its level band - but it is what a viewer is choosing between, so
        /// the editor nudges towards setting one.
        /// </summary>
        public string Name { get; set; } = "";

        public int LevelFrom { get; set; }
        public int LevelTo { get; set; }
        public List<SpawnEntry> Entries { get; set; } = new List<SpawnEntry>();

        /// <summary>
        /// Longest pack name kept. Names are shown on a small tile inside a 280px panel, and
        /// they round-trip through a flat config line, so there is no use for a long one.
        /// </summary>
        public const int MaxNameLength = 24;

        public bool Matches(int level)
        {
            return level >= LevelFrom && level <= LevelTo;
        }

        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? $"Lv {LevelFrom}-{LevelTo}" : Name; }
        }

        public override string ToString()
        {
            var entries = Entries.Count > 0
                ? string.Join(", ", Entries.Select(e => e.ToString()).ToArray())
                : "(empty)";
            // DisplayName already reads "Lv 1-3" when the pack is unnamed, so don't repeat it.
            var label = string.IsNullOrWhiteSpace(Name)
                ? DisplayName
                : $"{Name} [Lv {LevelFrom}-{LevelTo}]";
            return $"{label}: {entries}";
        }

        public static string Serialize(IEnumerable<SpawnPack> packs)
        {
            var parts = packs
                .Where(p => p.Entries.Count > 0)
                .Select(p => $"{SanitizeName(p.Name)}|{p.LevelFrom}-{p.LevelTo}|" +
                             string.Join(",", p.Entries.Select(e => $"{e.ResRef.ToLowerInvariant()}:{e.Amount}").ToArray()));
            return string.Join(";", parts.ToArray());
        }

        /// <summary>
        /// Strips everything the flat config format or the pack encoding would choke on. The
        /// separators (; | , :) and '=' must go, and so must anything outside plain ASCII - the
        /// name travels to viewers' browsers and back through a hand-rolled JSON reader, and
        /// nothing here is worth a charset bug.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";

            var sb = new StringBuilder(MaxNameLength);
            var lastWasSpace = true;

            foreach (var c in name)
            {
                if (c == ' ' || c == '\t')
                {
                    if (!lastWasSpace && sb.Length < MaxNameLength)
                        sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                      || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '\'';
                if (!ok)
                    continue;

                if (sb.Length >= MaxNameLength)
                    break;

                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString().Trim();
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

                // Two shapes are accepted: "from-to|entries" and "name|from-to|entries". The
                // first is what shipped before packs had names, and configs written by it are
                // still out there - a rename must not cost the streamer their packs.
                var halves = packText.Split('|');
                if (halves.Length != 2 && halves.Length != 3)
                    continue;

                var name = halves.Length == 3 ? SanitizeName(halves[0]) : "";
                var rangeText = halves[halves.Length - 2];
                var entriesText = halves[halves.Length - 1];

                var range = rangeText.Split('-');
                if (range.Length != 2)
                    continue;

                if (!int.TryParse(range[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                    continue;
                if (!int.TryParse(range[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
                    continue;

                var pack = new SpawnPack { Name = name, LevelFrom = from, LevelTo = to };

                foreach (var entryText in entriesText.Split(','))
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
        /// Stable per-pack ids, index-aligned with <paramref name="packs"/>. This is what a
        /// viewer's summon names, so it has to be derived the same way on the publishing side
        /// and the resolving side - hence one function used by both, rather than an id stored
        /// per pack that a config edit could desynchronise.
        ///
        /// Ids come from the name so they survive reordering, which a list index would not: a
        /// viewer picking the third tile must not summon something else because the streamer
        /// deleted a pack in between. Duplicate names are suffixed rather than rejected - the
        /// editor doesn't stop a streamer reusing one, and two tiles sharing an id would make
        /// one of them unreachable.
        /// </summary>
        public static List<string> AssignIds(IReadOnlyList<SpawnPack> packs)
        {
            var ids = new List<string>(packs.Count);
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pack in packs)
            {
                var baseId = slugify(pack.DisplayName);
                var id = baseId;
                var suffix = 2;
                while (used.Contains(id))
                    id = $"{baseId}-{suffix++}";

                used.Add(id);
                ids.Add(id);
            }

            return ids;
        }

        private static string slugify(string text)
        {
            var sb = new StringBuilder(32);
            var lastWasDash = true;

            foreach (var c in text.ToLowerInvariant())
            {
                var isAlnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (isAlnum)
                {
                    if (sb.Length >= 32)
                        break;
                    sb.Append(c);
                    lastWasDash = false;
                    continue;
                }

                if (!lastWasDash && sb.Length < 32)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }

            var slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? "pack" : slug;
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
