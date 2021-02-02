using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Linq;
using WinApiBindings;
namespace BGOverlay
{
    public class BGEntity
    {
        public CREReader Reader { get; set; }
        private ResourceManager resourceManager;

        public int Id { get; private set; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public int Type { get; private set; }
        public string Name1 { get; private set; }
        public string CreResourceFilename { get; private set; }

        public byte CurrentHP { get; private set; }

        public List<string> Protections 
        {
            get
            {
                var allEffects = this.Reader.Effects.Where(x => x.EffectName != Effect.Text_Protection_from_Display_Specific_String);
                var result = new List<String>();
                var opCodeStrings = new List<String>();
                var spellStrings = new List<String>();
                foreach (var item in allEffects)
                {
                    if ($"{item.EffectName}".StartsWith("Graphics")
                        || $"{item.EffectName}".StartsWith("Text"))
                    {
                        continue;
                    }
                    if (item.EffectName == Effect.Protection_from_Opcode)
                    {
                        opCodeStrings.Add(preprocess($"{(Effect)item.Param2}"));
                        continue;
                    }
                    if (item.EffectName == Effect.Spell_Protection_from_Spell)
                    {
                        var spellName = resourceManager.GetSPLReader($"{item.Resource.Trim('\0')}.SPL".ToUpper()).Name1;
                        if (spellName == "-1")
                        {
                            spellName = item.Resource;
                        }
                        spellStrings.Add(preprocess(spellName));
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_Proficiency_Modifier)
                    {
                        var amount = item.Param1;
                        var type = (Proficiency)item.Param2;
                        result.Add($"Proficiency {type} + {amount}");
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_AC_vs_Damage_Type_Modifier)
                    {
                        var amount = item.Param1;
                        var type = item.Param2;

                        switch(type)
                        {
                            case 0:
                                result.Add($"AC Bonus +{amount}");
                                break;
                            case 1:
                                result.Add($"AC vs Crushing +{amount}");
                                break;
                            case 2:
                                result.Add($"AC vs Missile +{amount}");
                                break;
                            case 4:
                                result.Add($"AC vs Piercing +{amount}");
                                break;
                            case 8:
                                result.Add($"AC vs Slashing +{amount}");
                                break;
                            case 16:
                                result.Add($"Set AC to {amount}");
                                break;
                        }                        
                        continue;
                    }
                    if (item.EffectName == Effect.Stat_THAC0_Modifier)
                    {
                        var amount = item.Param1;
                        switch (item.Param2)
                        {
                            case 0:
                                result.Add($"THAC0 +{amount}");
                                break;
                            case 1:
                                result.Add($"Set THAC0 to {amount}");
                                break;
                            case 2:
                                result.Add($"THAC0 +{amount}%");
                                break;
                        }
                        continue;
                    }
                    result.Add(preprocess(item.EffectName.ToString()));
                }
                if (opCodeStrings.Any())
                {
                    result.Add(preprocess("Protection from " + string.Join(", ", opCodeStrings)));
                }
                if (spellStrings.Any())
                {
                    result.Add(preprocess("Spell protections: " + string.Join(", ", spellStrings)));
                }
                return result;
            }
        }

        private static List<string> filter = new List<string>()
        {
            "State_",
            "Stat_",
            "Death_",
            "Spell_Effect_",
            "Spell_",
        };

        private string preprocess(string str)
        {
            if (str == null) { return "null"; }
            foreach (var pattern in filter)
            {
                str = str.Replace(pattern, "");
            }
            return str.Replace("_"," ");
        }

        public BGEntity()
        {

        }
        public BGEntity(ResourceManager resourceManager, IntPtr hProc, IntPtr entityIdPtr)
        {
            this.resourceManager = resourceManager;
            this.Id              = WinAPIBindings.ReadInt32(hProc, entityIdPtr);
            entityIdPtr += 0x4;
            this.Type                          = WinAPIBindings.ReadByte(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x004 }));
            this.X                             = WinAPIBindings.ReadInt32(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x008 }));
            this.Y                             = WinAPIBindings.ReadInt32(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x00C }));
            this.Name1                         = WinAPIBindings.ReadString(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x364 }));
            this.CreResourceFilename           = WinAPIBindings.ReadString(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x3FC })).Trim('*') + ".CRE";
            this.CurrentHP                     = (byte)WinAPIBindings.ReadByte(hProc, WinAPIBindings.FindDMAAddy(hProc, entityIdPtr, new int[] { 0x438 }));
            if (Type == 49)
            {
                this.Reader = resourceManager.GetCREReader(CreResourceFilename.ToUpper());
                if (Reader == null)
                {
                     this.Reader = resourceManager.GetCREReader(CreResourceFilename.ToUpper());

                }
            }            
        }

        public override string ToString()
        {
            return additionalInfo();
        }

        private string additionalInfo()
        {
            if (Reader == null)
            {
                return "NO .CRE INFO";
            }
            if (Reader.ShortName == "<NO TEXT>")
            {
                throw new Exception();
            }
            return $"{Reader.ShortName} HP:{CurrentHP}";
        }
    }
}
