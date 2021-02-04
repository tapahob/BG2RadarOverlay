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
        public short MaxHP = 0;
        public int[] spellImmuneLevel = new int[10];
        public List<CWeaponIdentification> WeaponImmune = new List<CWeaponIdentification>();

        public List<CGameEffect> EffectImmunes = new List<CGameEffect>();

        public CDerivedStats(IntPtr addr)
        {
            this.MaxHP = WinAPIBindings.ReadInt16(addr + 4);
            this.immunitiesSpellLevel(addr + 0x344);
            this.immunitiesWeapon(addr + 0x36C);
            this.immunitiesEffect(addr + 0x30C);
            //this.immunitiesSpells(addr + 0x5A0);
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
