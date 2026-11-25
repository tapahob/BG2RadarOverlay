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
        private readonly string weapon;
        private WeaponHitEffects(ResourceManager resources, string weapon) { this.resources = resources; this.weapon = weapon; }
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
            var parser = new WeaponHitEffects(resources, name);
            string file = name.ToUpperInvariant() + ".ITM";
            string text = string.Join("\n", parser.Ability(resources.ReadResource(file), name, ability, 56, skip));
            return new Summary { Text = text, Notes = string.Join("\n", parser.notes.Select(n => file + " · " + n)) };
        }

        private List<string> Ability(byte[] b, string name, int index, int stride, int skip = -1, bool onWielder = false)
        {
            var lines = new List<string>();
            if (!Fits(b, 0, 0x72) || index < 0 || index >= Word(b, 0x68)) return lines;
            long offset = (long)Int(b, 0x64) + index * stride;
            if (!Fits(b, offset, stride)) return lines;
            int first = Word(b, (int)offset + 32), count = Word(b, (int)offset + 30);
            long start = (long)Int(b, 0x6a) + first * 48L;
            if (!Fits(b, start, count * 48L)) return lines;
            var chapterLimits = new Dictionary<int, int>();
            var conditionalTargets = new HashSet<int>();
            var actions = new List<string>();
            string lastDetails = null, lastRoll = null, family = null;
            void Flush()
            {
                if (actions.Count == 0) return;
                lines.Add(Join(family, string.Join(", ", actions), lastDetails));
                actions.Clear();
            }
            for (int i = 0; i < count; ++i)
            {
                if (skip == first + i) continue;
                if (--remaining < 0) break;
                var e = new Entry(b, (int)start + i * 48);
                if (e.Chance == 0) continue;
                if ((e.Op == 318 || e.Op == 324) && string.Equals(e.Resource, name, StringComparison.OrdinalIgnoreCase))
                {
                    bool certain = e.Chance == 100 && (e.Save & 31) == 0 && e.Timing <= 2 && e.Dice == 0 && e.Sides == 0;
                    if (certain && TryChapterLimit(e, out int chapter))
                    {
                        if (!chapterLimits.TryGetValue(e.Target, out int previous) || chapter < previous)
                            chapterLimits[e.Target] = chapter;
                    }
                    else
                    {
                        conditionalTargets.Add(e.Target);
                        notes.Add(name + " · SPLPROT.2DA [" + e.P2 + "] · " + e.P1);
                    }
                    continue;
                }
                int block = (int)start + i * 48;
                int consumed = onWielder && e.Op == 112 && e.Target == 2 && ImmediateInventoryChange(e)
                    ? BreakageLength(b, block, count - i) : 0;
                // Recognize breakage before filtering the inventory operations that implement it.
                if (consumed == 0 && HiddenOnHit(e.Op)) continue;
                string details = Details(e);
                if (chapterLimits.TryGetValue(e.Target, out int limit)) details = Join(details, L("BeforeChapter", limit));
                if (conditionalTargets.Contains(e.Target)) details = Join(details, L("Conditional"));
                if (IsNested(e.Op))
                {
                    string nested = Nested(e, details);
                    if (nested.Length == 0) continue;
                    Flush();
                    lines.Add(nested);
                    continue;
                }
                string action = consumed > 0 ? L("Breaks") : Action(e);
                if (action.Length == 0) continue;
                if (consumed > 0) i += consumed - 1;
                string groupName = e.Op == 78 || e.Op == 25 ? RadarLocalization.Get("Effect_" + (Effect)e.Op) : null;
                if (groupName != null && action.StartsWith(groupName + " · ", StringComparison.Ordinal))
                    action = action.Substring(groupName.Length + 3);
                else groupName = null;
                string roll = e.Low + "/" + e.High;
                if (groupName == null || groupName != family || details != lastDetails || roll != lastRoll)
                    Flush();
                actions.Add(action);
                family = groupName;
                lastDetails = details;
                lastRoll = roll;
            }
            Flush();
            return lines;
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

        private static bool HiddenOnHit(int op)
        {
            switch (op)
            {
                case 7: case 8: case 9: case 41: case 50: case 51: case 52: case 61:
                case 139: case 140: case 141: case 142: case 174: case 215: case 240: case 327: case 330:
                case (int)Effect.Removal_Effects_specified_by_Resource:
                case (int)Effect.Removal_Remove_Secondary_Type:
                case (int)Effect.Set_AI_Script:
                case (int)Effect.Use_EFF_File_Cursed:
                case (int)Effect.EEex_Invoke_Lua:
                case (int)Effect.Stat_Gold:
                case (int)Effect.Item_Create_Magical_Weapon:
                case (int)Effect.Item_Remove_Item:
                case (int)Effect.Item_Create_Inventory_Item:
                case (int)Effect.Item_Remove_Inventory_Item:
                case (int)Effect.Item_Create_Item_in_Slot:
                case (int)Effect.Item_Create_Inventory_Item_days:
                    return true;
                default: return false;
            }
        }

        private static bool ImmediateInventoryChange(Entry e) => e.Timing == 1 && e.Chance == 100 &&
            e.Save == 0 && e.Dice == 0 && e.Sides == 0;

        // Some breakage spells remove several variants of the same weapon before creating
        // the broken item. Compare item identity fields rather than translated names.
        private int BreakageLength(byte[] data, int offset, int count)
        {
            bool removesWeapon = false;
            byte[] baseItem = null;
            for (int i = 0; i < count; i++)
            {
                var entry = new Entry(data, offset + i * 48);
                if (entry.Target != 2 || !ImmediateInventoryChange(entry)) break;
                if (entry.Op == 122 && entry.Resource == "MISC56" && removesWeapon)
                    return i + 1;
                if (entry.Op != 112 || !ValidRef(entry.Resource)) break;
                if (entry.Resource.Equals(weapon, StringComparison.OrdinalIgnoreCase))
                    removesWeapon = true;
                else
                {
                    baseItem = baseItem ?? resources.ReadResource(weapon.ToUpperInvariant() + ".ITM");
                    var other = resources.ReadResource(entry.Resource + ".ITM");
                    if (!Fits(baseItem, 0, 30) || !Fits(other, 0, 30) || Int(baseItem, 12) < 0 ||
                        Int(baseItem, 12) != Int(other, 12) || Word(baseItem, 28) != Word(other, 28)) break;
                }
            }
            return 0;
        }

        private static string Details(Entry e, bool wrapper = false)
        {
            var details = new List<string>();
            if (!wrapper && e.Target != 2) details.Add(e.Target >= 1 && e.Target <= 9 ? L("Target" + e.Target) : L("Conditional"));
            if (e.Chance < 100) details.Add(e.Chance + "%");
            if (!wrapper && e.Duration > 0)
            {
                if (e.Op == 67 && e.Timing == 0 && e.Duration % 6 == 0)
                    details.Add(L("Rounds", e.Duration / 6));
                else if (e.Timing == 0 || e.Timing == 10)
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
            if (!RadarLocalization.TryGet("Effect_" + (Effect)e.Op, out string title) && e.Op != 12)
            {
                notes.Add("Opcode " + e.Op + " · P1=" + e.P1 + " · P2=" + e.P2);
                return L("UnknownEffect", e.Op);
            }
            switch (e.Op)
            {
                case (int)Effect.Stat_AC_vs_Damage_Type_Modifier:
                    return RadarLocalization.FormatArmorClassModifier(e.P2, e.P1) ?? title;
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
                case 235:
                    switch (e.P2)
                    {
                        case 0: case 1: return L("PushFromPoint");
                        case 2: return L("PushFromSource");
                        case 3: return L("PullToPoint");
                        case 4: return L("PullToSource");
                        default: return title;
                    }
                case 60:
                    if (e.P2 < 0 || e.P2 > 5) return title;
                    string[] failures = { "ArcaneFailure", "DivineFailure", "InnateFailure" };
                    return L(failures[e.P2 % 3]) + " " + L("PercentagePoints", Signed(e.P1));
                case 67:
                    if (!ValidRef(e.Resource)) return title;
                    var creature = resources.ReadResource(e.Resource + ".CRE");
                    string creatureName = null;
                    if (Fits(creature, 0, 16) && Encoding.ASCII.GetString(creature, 0, 4) == "CRE ")
                    {
                        creatureName = BGEntity.DisplayName(resources.GetStrRefText(Int(creature, 8)))
                            ?? BGEntity.DisplayName(resources.GetStrRefText(Int(creature, 12)));
                    }
                    return Join(title, creatureName ?? e.Resource);
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
                    return StatModifier(e, title);
                case 33: case 34: case 35: case 36: case 37: case 325:
                    return e.P2 == 3 ? title + " " + Signed(e.P1) : StatModifier(e, title);
                case 27: case 28: case 29: case 30: case 31:
                case 84: case 85: case 86: case 87: case 88: case 89:
                    return StatModifier(e, title, percentStat: true);
                case 166:
                    return e.P2 == 0 || e.P2 == 1 ? StatModifier(e, title, percentStat: true) : title;
                case 173:
                    return title + " " + L("PercentagePoints", Signed(e.P1));
                case 55: case 109: case 175: case 185:
                    return e.P1 == 0 ? title : Join(title, L("Only", Ids(e.P2, e.P1)));
                case 318: case 324: return Join(title, e.Resource, L("Only", Protection(e.P2, e.P1)));
                default: return title;
            }
        }

        private static string StatModifier(Entry e, string title, bool percentStat = false)
        {
            switch (e.P2)
            {
                case 0: return title + " " + (percentStat ? L("PercentagePoints", Signed(e.P1)) : Signed(e.P1));
                case 1: return title + " = " + e.P1 + (percentStat ? "%" : "");
                case 2: return title + " × " + e.P1 + "%";
                default: return title;
            }
        }

        private static bool IsNested(int op) => op == 177 || op == 146 || op == 148 || op == 326;

        private string Nested(Entry e, string details)
        {
            bool external = e.Op == 177;
            string file = e.Resource + (external ? ".EFF" : ".SPL");
            string only = external && e.P1 != 0 ? L("Only", Ids(e.P2, e.P1)) :
                e.Op == 326 ? L("Only", Protection(e.P2, e.P1)) : null;
            string unresolved() => Join(Action(e), ValidRef(e.Resource) ? file : null, only, details);
            if (!ValidRef(e.Resource) || active.Count >= 8 || !active.Add(file))
                return unresolved();
            try
            {
                var b = resources.ReadResource(file);
                if (external && Fits(b, 0, 104) && Encoding.ASCII.GetString(b,0,8) == "EFF V2.0")
                {
                    var inner = new Entry(b,e);
                    if (HiddenOnHit(inner.Op) || inner.Chance == 0) return "";
                    string innerDetails = Join(Details(inner, true), only, details);
                    if (IsNested(inner.Op)) return Nested(inner, innerDetails);
                    string action = Action(inner);
                    return action.Length == 0 ? "" : Join(action, innerDetails);
                }
                if (!external && Fits(b,0,0x72) && Encoding.ASCII.GetString(b,0,4) == "SPL ")
                {
                    string name = resources.GetStrRefText(Int(b,8));
                    bool unnamed = name == "-1" || name == "<NO IDEA>" || string.IsNullOrWhiteSpace(name);
                    // A level-dependent spell cannot be flattened without its effective casting level.
                    if (Word(b,0x68) != 1 || Word(b,0x70) != 0)
                    {
                        if (unnamed) return unresolved();
                        notes.Add(file);
                        return Join(name, only, L("Conditional"), details);
                    }
                    var children = Ability(b, e.Resource, 0, 40,
                        onWielder: active.Count == 1 && e.Op == 146 && e.Target == 1);
                    if (children.Count == 0) return "";
                    string heading = Join(unnamed ? null : name, only, details);
                    string child = string.Join("\n", children);
                    if (heading.Length == 0) return child;
                    if (unnamed && !child.Contains("\n")) return Join(child, heading);
                    // A condition shared by several outcomes is one roll for the whole group.
                    return heading + "\n    " + child.Replace("\n", "\n    ");
                }
                return unresolved();
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

        private bool TryChapterLimit(Entry e, out int chapter)
        {
            chapter = 0;
            var rows = Table("SPLPROT.2DA");
            if (e.P2 < 0 || e.P2 >= rows.Length - 3) return false;
            var row = rows[e.P2 + 3];
            if (row.Length < 4 || !Number(row[1], out int stat) || stat != 0x116 ||
                !Number(row[2], out chapter) || !Number(row[3], out int relation) || relation != 4) return false;
            if (chapter == -1) chapter = e.P1;
            return true;
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
            if (stat == 0x116 && rel == 4) return L("ChapterFrom", value);
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
