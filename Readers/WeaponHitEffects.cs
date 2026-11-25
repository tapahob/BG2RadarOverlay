using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BGOverlay.Resources;

namespace BGOverlay
{
    // Resolve a single attack lazily, against the installed game's KEY and override resources.
    // Each recursive call owns its guards: restrictions on one subspell must not leak to siblings.
    internal sealed class WeaponHitEffects
    {
        private readonly ResourceManager resources;
        private readonly HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[][]> tables = new Dictionary<string, string[][]>();
        private int remaining = 512;
        private readonly HashSet<string> notes = new HashSet<string>();
        internal sealed class Summary { internal string Text, Notes; }
        private string Unknown(string reason)
        {
            notes.Add(reason);
            return L("Unknown");
        }

        private WeaponHitEffects(ResourceManager resources) { this.resources = resources; }
        private static string L(string key, params object[] args) =>
            string.Format(RadarLocalization.Get("Hit_" + key), args);
        private static int Word(byte[] b, int p) => BitConverter.ToUInt16(b, p);
        private static int Int(byte[] b, int p) => BitConverter.ToInt32(b, p);
        private static bool Fits(byte[] b, long offset, long size) =>
            b != null && offset >= 0 && size >= 0 && offset + size <= b.Length;
        private static string Ref(byte[] b, int p) =>
            Encoding.ASCII.GetString(b, p, 8).Split('\0')[0].ToUpperInvariant();
        private static bool ValidRef(string name) => name.Length > 0 && name.Length <= 8 &&
            name.All(c => c >= ' ' && "\\/:*?\"<>|".IndexOf(c) < 0);
        private static string Join(params string[] fields) =>
            string.Join(" · ", fields.Where(x => !string.IsNullOrWhiteSpace(x)));
        private static string Signed(int n) => n.ToString("+0;-0;0");

        public static Summary Describe(ResourceManager resources, string name, int ability, int skip)
        {
            var parser = new WeaponHitEffects(resources);
            string file = name.ToUpperInvariant() + ".ITM";
            string text = parser.Ability(resources.ReadResource(file), name, ability, 56, skip);
            return new Summary { Text = text, Notes = string.Join("\n", parser.notes.Select(n => file + " · " + n)) };
        }

        private string Ability(byte[] b, string name, int index, int stride, int skip = -1)
        {
            if (!Fits(b, 0, 0x72) || index < 0 || index >= Word(b, 0x68)) return L("Unknown");
            long offset = (long)Int(b, 0x64) + index * stride;
            if (!Fits(b, offset, stride)) return L("Unknown");
            int first = Word(b, (int)offset + 32), count = Word(b, (int)offset + 30);
            long start = (long)Int(b, 0x6a) + first * 48L;
            if (!Fits(b, start, count * 48L)) return L("Unknown");
            var lines = new List<string>();
            var guards = new Dictionary<int, List<string>>();
            var actions = new List<string>();
            string lastDetails = null, lastExcluded = null, lastRoll = null, family = null;
            void Flush()
            {
                if (actions.Count == 0) return;
                lines.Add(Join(family, string.Join(", ", actions), lastDetails));
                if (!string.IsNullOrEmpty(lastExcluded)) lines.Add(lastExcluded);
                actions.Clear();
            }
            for (int i = 0; i < count; ++i)
            {
                if (skip == first + i) continue;
                if (--remaining < 0) { Flush(); lines.Add(L("Unknown")); break; }
                var e = new Entry(b, (int)start + i * 48);
                if (e.Chance == 0 || Cosmetic(e.Op)) continue;
                if ((e.Op == 318 || e.Op == 324) && string.Equals(e.Resource, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!guards.TryGetValue(e.Target, out var restrictions))
                        guards.Add(e.Target, restrictions = new List<string>());
                    string restriction = Protection(e.P2, e.P1);
                    restrictions.Add(e.Chance == 100 && (e.Save & 31) == 0 && e.Timing <= 2
                        ? restriction : L("Conditional"));
                    continue;
                }
                string action = Action(e);
                if (string.IsNullOrEmpty(action)) continue;
                string details = Details(e);
                if (action.Contains("\n") && details.Length > 0)
                {
                    int line = action.IndexOf('\n');
                    action = Join(action.Substring(0, line), details) + action.Substring(line);
                    details = "";
                }
                string groupName = e.Op == 78 || e.Op == 25 ? RadarLocalization.Get("Effect_" + (Effect)e.Op) : null;
                if (groupName != null && action.StartsWith(groupName + " · ", StringComparison.Ordinal))
                    action = action.Substring(groupName.Length + 3);
                else groupName = null;
                string excluded = guards.TryGetValue(e.Target, out var group)
                    ? L("Unaffected", string.Join(", ", group.Distinct())) : null;
                string roll = e.Low + "/" + e.High;
                if (groupName == null || groupName != family || details != lastDetails || excluded != lastExcluded || roll != lastRoll)
                    Flush();
                actions.Add(action);
                family = groupName;
                lastDetails = details;
                lastExcluded = excluded;
                lastRoll = roll;
                if (action.Contains("\n")) Flush();
            }
            Flush();
            return string.Join("\n", lines);
        }

        private sealed class Entry
        {
            internal int Op, Target, P1, P2, Timing, Duration, Low, High, Dice, Sides, Save, Bonus, Frequency, Special;
            internal string Resource;
            internal int Chance => Math.Max(0, Math.Min(99, High) - Low + 1);
            internal Entry(byte[] b, int p)
            {
                Op = Word(b,p); Target = b[p+2]; P1 = Int(b,p+4); P2 = Int(b,p+8);
                Timing = b[p+12]; Duration = Int(b,p+14); High = b[p+18]; Low = b[p+19];
                Resource = Ref(b,p+20); Dice = Int(b,p+28); Sides = Int(b,p+32);
                Save = Int(b,p+36); Bonus = Int(b,p+40); Special = Int(b,p+44);
            }
            internal Entry(byte[] b, Entry parent)
            {
                Op = Int(b,16); P1 = Int(b,28); P2 = Int(b,32);
                Target = parent.Target; Timing = parent.Timing; Duration = parent.Duration;
                High = Word(b,44); Low = Word(b,46); Resource = Ref(b,48);
                Dice = Int(b,56); Sides = Int(b,60); Save = Int(b,64); Bonus = Int(b,68);
                Special = Int(b,72); Frequency = Int(b,100);
            }
        }

        private static bool Cosmetic(int op)
        {
            switch (op)
            {
                case 7: case 8: case 9: case 41: case 50: case 51: case 52: case 61:
                case 139: case 140: case 141: case 142: case 174: case 215: case 240: case 327: case 330:
                    return true;
                default: return false;
            }
        }

        private static string Details(Entry e, bool wrapper = false)
        {
            var details = new List<string>();
            if (!wrapper && e.Target != 2) details.Add(e.Target >= 1 && e.Target <= 9 ? L("Target" + e.Target) : L("Conditional"));
            if (e.Chance < 100) details.Add(e.Chance + "%");
            if (!wrapper && e.Duration > 0)
            {
                if (e.Timing == 0 || e.Timing == 10)
                    details.Add(L("Seconds", e.Timing == 10 ? e.Duration / 15.0 : e.Duration));
                else if (e.Timing >= 3 && e.Timing <= 5) details.Add(L("After", e.Duration));
            }
            string[] saves = { "Spell", "Breath", "Death", "Wand", "Poly" };
            var types = saves.Where((key, i) => (e.Save & (1 << i)) != 0).Select(key => L(key)).ToList();
            if (types.Count > 0) details.Add(L("Save", string.Join("/", types)) + (e.Bonus == 0 ? "" : " " + Signed(e.Bonus)));
            else if (!wrapper && (e.Op == 78 || e.Op == 25 || e.Op == 109 || e.Op == 45)) details.Add(L("NoSave"));
            if (e.Op != 12 && e.Op != 17 && e.Op != 18 && e.Op != 218 && e.Op != 331 && e.Op != 326)
            {
                if (e.Dice > 0) details.Add(L("MaxLevel", e.Dice));
                if (e.Sides > 0) details.Add(L("MinLevel", e.Sides));
            }
            return Join(details.ToArray());
        }

        private string Action(Entry e)
        {
            string title;
            if (!RadarLocalization.TryGet("Effect_" + (Effect)e.Op, out title))
                title = Unknown("opcode " + e.Op + " · " + e.Resource);
            switch (e.Op)
            {
                case 12:
                    int kind = (int)((uint)e.P2 >> 16);
                    string type;
                    if (!RadarLocalization.TryGet("Hit_Damage" + kind, out type)) type = L("Damage");
                    if ((e.P2 & 0xffff) != 0) return Join(type, L("Conditional"));
                    string roll = e.Dice > 0 && e.Sides > 0 ? e.Dice + "d" + e.Sides : "";
                    string amount = roll.Length == 0 ? e.P1.ToString() : roll + (e.P1 == 0 ? "" : Signed(e.P1));
                    return Join(type + " " + amount, (e.Special & 9) != 0 ? L("Drain") : null,
                        (e.Special & 18) != 0 ? L("ReverseDrain") : null,
                        (e.Special & 256) != 0 ? L("Half") : null);
                case 17:
                    if ((e.P2 & 0xffff) == 0 && e.Dice == 0) return L("HitPoints", Signed(e.P1));
                    return title;
                case 216: return title + " " + e.P1;
                case 25: case 78:
                    long frequency = Math.Max(1, e.Frequency);
                    if (e.P2 == 2) return Join(title, frequency == 1 ? L("PerSecond", e.P1) : L("Periodic", e.P1, frequency));
                    if (e.P2 == 3 && e.P1 > 0) return Join(title, L("Periodic", 1, e.P1 * frequency));
                    if (e.Op == 78 && e.P2 == 10) return Join(title, RadarLocalization.Get("Effect_State_Slow"));
                    if (e.Op == 78 && e.P2 >= 4 && e.P2 <= 9)
                    {
                        int[] stats = { 44, 15, 10, 19, 49, 6 };
                        return Join(title, RadarLocalization.Get("Effect_" + (Effect)stats[e.P2 - 4]) + " " + Signed(-e.P1));
                    }
                    return title;
                case 6: case 10: case 15: case 19: case 44: case 49: case 54:
                    if (e.P2 == 0) return title + " " + Signed(e.P1);
                    if (e.P2 == 1) return title + " = " + e.P1;
                    if (e.P2 == 2) return title + " × " + e.P1 + "%";
                    return title;
                case 55: case 109: case 175: case 185:
                    return e.P1 == 0 ? title : Join(title, L("Only", Ids(e.P2, e.P1)));
                case 177: case 146: case 148: case 326:
                    return Nested(e);
                case 318: case 324: return Join(title, e.Resource, L("Only", Protection(e.P2, e.P1)));
                case 402: return Unknown("402 · " + e.Resource);
                default: return title;
            }
        }

        private string Nested(Entry e)
        {
            bool external = e.Op == 177;
            string file = e.Resource + (external ? ".EFF" : ".SPL");
            string only = external && e.P1 != 0 ? L("Only", Ids(e.P2, e.P1)) :
                e.Op == 326 ? L("Only", Protection(e.P2, e.P1)) : null;
            if (!ValidRef(e.Resource) || active.Count >= 8 || !active.Add(file))
                return Join(Unknown(file), only);
            try
            {
                var b = resources.ReadResource(file);
                if (external && Fits(b, 0, 104) && Encoding.ASCII.GetString(b,0,8) == "EFF V2.0")
                {
                    var inner = new Entry(b,e);
                    if (Cosmetic(inner.Op) || inner.Chance == 0) return "";
                    return Join(Action(inner), Details(inner, true), only);
                }
                if (!external && Fits(b,0,0x72) && Encoding.ASCII.GetString(b,0,4) == "SPL ")
                {
                    string name = resources.GetStrRefText(Int(b,8));
                    string label = name == "-1" || name == "<NO IDEA>" || string.IsNullOrWhiteSpace(name) ? L("SpellEffect") : name;
                    // A level-dependent spell cannot be flattened without its effective casting level.
                    if (Word(b,0x68) != 1 || Word(b,0x70) != 0)
                    {
                        notes.Add(file);
                        return Join(label, only, L("Conditional"));
                    }
                    var child = Ability(b, e.Resource, 0, 40);
                    return string.IsNullOrEmpty(child) ? "" : Join(label, only) + "\n    " + child.Replace("\n", "\n    ");
                }
                return Join(Unknown(file), only);
            }
            finally { active.Remove(file); }
        }

        private string[][] Table(string file)
        {
            if (!tables.TryGetValue(file, out var rows))
            {
                byte[] b = resources.ReadResource(file);
                rows = b == null ? Array.Empty<string[]>() : Encoding.ASCII.GetString(b)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim())
                    .Where(l => l.Length > 0).Select(l => l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
                tables.Add(file, rows);
            }
            return rows;
        }

        private static bool Number(string s, out int n) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)
            : int.TryParse(s, out n);

        private string Ids(int file, int value)
        {
            string[] files = { "EA", "GENERAL", "RACE", "CLASS", "SPECIFIC", "GENDER", "ALIGN", "KIT" };
            if (file < 2 || file > 9) return L("Conditional");
            string table = files[file - 2];
            var row = Table(table + ".IDS").FirstOrDefault(r => r.Length >= 2 && Number(r[0], out var n) && n == value);
            if (row == null) return L("Conditional");
            string symbol = row[1];
            if (RadarLocalization.TryGet("Hit_" + symbol, out var localized) ||
                RadarLocalization.TryGet((file == 4 ? "Race_" : file == 5 ? "Class_" : "Alignment_") + symbol, out localized))
                return localized;
            return symbol.Replace('_', ' ').ToLowerInvariant();
        }

        private string Protection(int index, int parameter, int depth = 0)
        {
            var table = Table("SPLPROT.2DA");
            if (index < 0 || index >= table.Length - 3 || depth > 8) return L("Conditional");
            var row = table[index + 3];
            if (row.Length < 4 || !Number(row[1], out int stat) || !Number(row[2], out int value) || !Number(row[3], out int rel))
                return L("Conditional");
            if (stat == 0x103) return L("Or", Protection(value, parameter, depth + 1), Protection(rel, parameter, depth + 1));
            if (value == -1) value = parameter;
            if (rel == 1 && stat >= 0x10a && stat <= 0x10f) return Ids(stat - 0x10a + 2, value);
            if (stat == 74 && rel == 4) return L("PoisonResistance", value);
            if (stat == 0x110 && rel == 1) return Ids(8, value);
            // SPLPROT uses STATS.IDS below 0x100. Read mod-defined rows from the active installation.
            if (stat >= 0 && stat < 256 && rel >= 0 && rel <= 5)
            {
                string[] operators = { "≤", "=", "<", ">", "≥", "≠" };
                var entry = Table("STATS.IDS").FirstOrDefault(r => r.Length >= 2 && Number(r[0], out int id) && id == stat);
                if (entry != null) return entry[1].Replace('_', ' ').ToLowerInvariant() + " " + operators[rel] + " " + value;
            }
            notes.Add("SPLPROT.2DA · " + index);
            return L("Conditional");
        }
    }
}
