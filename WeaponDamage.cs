using System;
using BGOverlay.Resources;
using static WinApiBindings.WinAPIBindings;

namespace BGOverlay
{
    internal static class WeaponDamage
    {
        // A launcher can be loaded by the engine from a DLC archive that our ITM reader
        // cannot open. Read CItem::pRes and the parsed CResItem's first ability in that case.
        internal static (int Bonus, bool UsesStrength, int IdentifiedNameRef, int GeneralNameRef, string IconResource)?
            ReadLauncher(IntPtr slot, int itemType)
        {
            IntPtr item = ReadPointer(slot);
            if (item == IntPtr.Zero) return null;
            IntPtr resource = ReadPointer(item + 8);
            if (resource == IntPtr.Zero || ReadByte(resource + 0x51) == 0) return null;

            IntPtr header = ReadPointer(resource + 0x68);
            if (header == IntPtr.Zero || ReadUInt16(header + 0x1C) != itemType ||
                ReadUInt16(header + 0x68) == 0) return null;

            IntPtr ability = ReadPointer(resource + 0x58);
            var data = new byte[0x27];
            if (ability == IntPtr.Zero || !ReadInto(ability, data, data.Length) || data[0] == 0)
                return null;
            return (BitConverter.ToInt16(data, 0x1A), (data[0x26] & 5) != 0,
                ReadInt32(header + 0x0C), ReadInt32(header + 0x08), ReadString(header + 0x3A, 8));
        }

        // C2DArray, column 1 (DAMAGE) in the live STRMOD and STRMODEX tables.
        // Use the engine's loaded rules so modded tables work too.
        private static int? StrengthBonus(IntPtr table, int row)
        {
            int columns = ReadInt16(table + 0x30);
            int rows = ReadInt16(table + 0x32);
            IntPtr cell = table + 0x28; // default value for out-of-range stats
            if (columns > 1 && row >= 0 && row < rows)
                cell = ReadPointer(table + 0x20) + (row * columns + 1) * 8;
            return int.TryParse(ReadString(ReadPointer(cell)), out int value) ? value : (int?)null;
        }

        private static int Roll(ITMReader.WeaponAbility ability, int luck, bool maximum)
        {
            if (ability == null) return 0;
            int die = maximum ? ability.Sides : 1;
            die = Math.Max(1, Math.Min(ability.Sides, die + luck));
            return ability.Bonus + ability.Dice * die;
        }

        private static int ApplyPercent(int damage, int percent) => damage + damage * percent / 100;

        // Opcode 332 bonuses are percentages. For a weapon with two possible physical
        // damage types the engine picks one based on the victim's resistances.
        private static void TypeBonuses(byte[] stats, DamageType type, out int low, out int high)
        {
            int Bonus(int index) => BitConverter.ToInt16(stats, 0x1F6 + 2 * index);
            int crush = Bonus(9), pierce = Bonus(8), slash = Bonus(7);
            low = high = crush;
            switch (type)
            {
                case DamageType.Piercing: low = high = pierce; break;
                case DamageType.Slashing: low = high = slash; break;
                case DamageType.Missile: low = high = Bonus(10); break;
                case DamageType.Piercing_or_Crushing: low = Math.Min(pierce, crush); high = Math.Max(pierce, crush); break;
                case DamageType.Piercing_or_Slashing: low = Math.Min(pierce, slash); high = Math.Max(pierce, slash); break;
                case DamageType.Crushing_or_Slashing: low = Math.Min(crush, slash); high = Math.Max(crush, slash); break;
            }
        }

        internal static string Read(IntPtr sprite, IntPtr game, ITMReader.WeaponAbility weapon,
            (int Bonus, bool UsesStrength)? launcher, int characterId, bool offHand = false)
        {
            if (sprite == IntPtr.Zero || game == IntPtr.Zero || weapon == null) return null;
            if (weapon.Dice == 0 && weapon.Bonus == 0 &&
                (launcher?.Bonus ?? 0) == 0) return "0–0";
            // CGameSprite::GetActiveStats switches buffers during effect processing.
            var stats = new byte[0x20C];
            int statsOffset = ReadInt32(sprite + 0x4EA4) != 0 ? 0x1120 : 0x1DC8;
            if (!ReadInto(sprite + statsOffset, stats, stats.Length)) return null;

            // CheckCombatStats has already applied proficiency, style and weapon-specific
            // buffs to m_DamageBonusRight/Left. CGameSprite::Damage adds strength separately.
            int bonus = BitConverter.ToInt16(stats, 0x80) + BitConverter.ToInt16(stats, offHand ? 0xF0 : 0xEC);
            if (weapon.UsesStrength || launcher?.UsesStrength == true)
            {
                int? strength = StrengthBonus(game + 0xC08, BitConverter.ToInt16(stats, 0x4E))
                              + StrengthBonus(game + 0xC40, BitConverter.ToInt16(stats, 0x50));
                if (!strength.HasValue) return null;
                bonus += strength.Value;
            }
            int luck = BitConverter.ToInt16(stats, 0x42) + BitConverter.ToInt32(stats, 0x1CC);
            TypeBonuses(stats, weapon.DamageType, out int lowPercent, out int highPercent);
            int Bound(bool maximum, int percent)
            {
                // The engine's displayed range uses the projectile's dice and the launcher's flat bonus.
                int roll = Roll(weapon, luck, maximum) + (launcher?.Bonus ?? 0);
                int damage = Math.Max(weapon.Dice > 0 ? 1 : 0, roll + bonus);
                return Math.Max(0, ApplyPercent(ApplyPercent(damage, BitConverter.ToInt16(stats, 0x1F6)), percent));
            }
            int min = Bound(false, lowPercent), max = Bound(true, highPercent);
            // CInfGame::GetCharacterPortraitNum checks these six object IDs.
            // This remains accurate when charm changes the character's allegiance.
            var portraits = new byte[6 * sizeof(int)];
            if (!ReadInto(game + 0x6618, portraits, portraits.Length)) return null;
            for (int offset = 0; offset < portraits.Length; offset += sizeof(int))
                if (BitConverter.ToInt32(portraits, offset) == characterId)
                    return $"{min}–{max}";

            // Read both multipliers and their switches together from CInfGame::m_options.
            const int firstOption = 0x78;
            var options = new byte[0x1FC - firstOption];
            if (!ReadInto(game + 0x7CD0 + firstOption, options, options.Length)) return null;
            int Option(int offset) => BitConverter.ToInt32(options, offset - firstOption);
            if (Option(0x80) == 0) // m_bNoExtraDamage
            {
                // SetDifficulty mirrors the local percentage to the MP field in solo play;
                // on a client that field carries the host's value. Story and Nightmare
                // override the local percentage at the end of InitDifficulty.
                bool forced = Option(0x1F4) == 1 || Option(0x1F8) == 1;
                int percent = Option(forced ? 0x78 : 0x7C);
                min = ApplyPercent(min, percent);
                max = ApplyPercent(max, percent);
            }
            return $"{min}–{max}";
        }
    }
}
