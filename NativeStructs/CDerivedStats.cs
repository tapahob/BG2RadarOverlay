using BGOverlay.NativeStructs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WinApiBindings;

namespace BGOverlay
{
    public class CDerivedStats
    {
        public uint GeneralState { get; private set; }
        public short MaxHP { get; private set; }
        public short ArmorClass { get; private set; }
        public short THAC0 { get; private set; }
        public short HitBonus { get; private set; }
        public int THAC0BonusRight { get; private set; }
        public short NumberOfAttacks { get; private set; }
        public short SaveVsDeath { get; private set; }
        public short SaveVsWands { get; private set; }
        public short SaveVsPoly { get; private set; }
        public short SaveVsBreath { get; private set; }
        public short SaveVsSpell { get; private set; }
        public short ResistFire { get; private set; }
        public short ResistCold { get; private set; }
        public short ResistElectricity { get; private set; }
        public short ResistAcid { get; private set; }
        public short ResistMagic { get; private set; }
        public short ResistPoison { get; private set; }
        public short ResistMagicDamage { get; private set; }
        public short ResistSlashing { get; private set; }
        public short ResistCrushing { get; private set; }
        public short ResistPiercing { get; private set; }
        public short ResistMissile { get; private set; }
        public short STR { get; private set; }
        public short INT { get; private set; }
        public short WIS { get; private set; }
        public short DEX { get; private set; }
        public short CON { get; private set; }
        public short CHA { get; private set; }
        public short Level1 { get; private set; }
        public short Level2 { get; private set; }
        public short Level3 { get; private set; }
        public string Level { get; private set; }
        public int BackstabImmunity { get; private set; }
        public int SeeInvisible { get; private set; }

        public int[] spellImmuneLevel = new int[10];
        public List<CWeaponIdentification> WeaponImmune = new List<CWeaponIdentification>();

        public List<CGameEffect> EffectImmunes = new List<CGameEffect>();
        public List<CImmunitySpell> SpellImmunities = new List<CImmunitySpell>();

        public CDerivedStats(IntPtr addr)
        {
            this.GeneralState = WinAPIBindings.ReadUInt32(addr);
            this.MaxHP = WinAPIBindings.ReadInt16(addr + 4);            
            this.ArmorClass = WinAPIBindings.ReadInt16(addr + 0x06);
            this.THAC0 = WinAPIBindings.ReadInt16(addr + 0x10);
            this.HitBonus = WinAPIBindings.ReadInt16(addr + 0x1F4);
            this.THAC0BonusRight = WinAPIBindings.ReadInt32(addr + 0xE4);
            this.NumberOfAttacks = WinAPIBindings.ReadInt16(addr + 0x12);
            this.SaveVsDeath = WinAPIBindings.ReadInt16(addr + 0x14);
            this.SaveVsWands = WinAPIBindings.ReadInt16(addr + 0x16);
            this.SaveVsPoly = WinAPIBindings.ReadInt16(addr + 0x18);
            this.SaveVsBreath = WinAPIBindings.ReadInt16(addr + 0x1A);
            this.SaveVsSpell = WinAPIBindings.ReadInt16(addr + 0x1C);

            this.ResistFire = WinAPIBindings.ReadInt16(addr + 0x1E);
            this.ResistCold = WinAPIBindings.ReadInt16(addr + 0x1E + 2);
            this.ResistElectricity = WinAPIBindings.ReadInt16(addr + 0x22);
            this.ResistAcid = WinAPIBindings.ReadInt16(addr + 0x24);
            this.ResistMagic = WinAPIBindings.ReadInt16(addr + 0x26);
            this.ResistPoison = WinAPIBindings.ReadInt16(addr + 0xC0);
            this.ResistMagicDamage = WinAPIBindings.ReadInt16(addr + 0xBE);

            this.ResistSlashing = WinAPIBindings.ReadInt16(addr + 0x2C);
            this.ResistCrushing = WinAPIBindings.ReadInt16(addr + 0x2E);
            this.ResistPiercing = WinAPIBindings.ReadInt16(addr + 0x30);
            this.ResistMissile = WinAPIBindings.ReadInt16(addr + 0x32);

            this.STR = WinAPIBindings.ReadInt16(addr + 0x4E);
            this.INT = WinAPIBindings.ReadInt16(addr + 0x52);
            this.WIS = WinAPIBindings.ReadInt16(addr + 0x54);
            this.DEX = WinAPIBindings.ReadInt16(addr + 0x56);
            this.CON = WinAPIBindings.ReadInt16(addr + 0x58);
            this.CHA = WinAPIBindings.ReadInt16(addr + 0x5A);

            this.Level1 = WinAPIBindings.ReadInt16(addr + 0x46);
            this.Level2 = WinAPIBindings.ReadInt16(addr + 0x48);
            this.Level3 = WinAPIBindings.ReadInt16(addr + 0x4A);

            this.Level = "" + this.Level1;
            if (this.Level2 > 1)
            {
                this.Level = this.Level + "/" + this.Level2;
            }
            if (this.Level3 > 1)
            {
                this.Level = this.Level + "/" + this.Level3;
            }

            this.BackstabImmunity = WinAPIBindings.ReadInt32(addr + 0x284);
            this.SeeInvisible = WinAPIBindings.ReadInt32(addr + 0xD8);

            this.immunitiesSpellLevel(addr + 0x344);
            this.immunitiesWeapon(addr + 0x36C);
            this.immunitiesEffect(addr + 0x30C);
            this.immunitiesSpells(addr + 0x5A0);
        }

        private void immunitiesEffect(IntPtr intPtr)
        {
            var list = new CPtrList(intPtr);
            var count = list.Count;
            if (count > 100)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.EffectImmunes.Add(new CGameEffect(node.Data));

                node = node.getNext();
            }
        }

        private void immunitiesSpells(IntPtr intPtr)
        {
            var list = new CPtrList(intPtr);
            var count = list.Count;
            if (count > 100)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.SpellImmunities.Add(new CImmunitySpell(node.Data));
                node = node.getNext();
            }
        }

        private void immunitiesSpellLevel(IntPtr intPtr)
        {
            for (int i= 0; i<10; ++i)
            {
                this.spellImmuneLevel[i] = WinAPIBindings.ReadInt32(intPtr + i * 0x4);
            }
        }

        private void immunitiesWeapon(IntPtr intPtr)
        {
            var list = new CPtrList(intPtr);
            var count = list.Count;
            if (count > 100)
                return;
            var node = list.Head;
            for (int i = 0; i < count; ++i)
            {
                this.WeaponImmune.Add(new CWeaponIdentification(node.Data));

                node = node.getNext();
            }
        }
    }
}
