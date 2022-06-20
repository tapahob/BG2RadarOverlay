using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

namespace BGOverlay
{
    // reference: https://gibberlings3.github.io/iesdp/file_formats/ie_formats/cre_v1.htm
    public class ITMReader
    {
        public static string gameDirectory = Configuration.GameFolder;
        private ResourceManager resourceManager;

        public ITMReader(ResourceManager resourceManager, String itmFilename = "GHAST1.ITM", int originOffset = 0, string biffArchivePath = "")
        {
            this.resourceManager = resourceManager;
            var filename = itmFilename;
            var overrideItmFilename = $"{gameDirectory}\\override\\{itmFilename}";
            if (File.Exists(overrideItmFilename))
            {
                originOffset = 0;
                biffArchivePath = "";
                filename = overrideItmFilename;
            }
            else
            {
                var lastTry = Directory.Exists($"{gameDirectory}\\override")
                    ? Directory.GetFiles($"{gameDirectory}\\override").Select(x => x.ToUpper()).FirstOrDefault(x => x.EndsWith(itmFilename))
                    : null;
                if (lastTry == null)
                {
                    filename = biffArchivePath;
                    if (biffArchivePath.Equals(""))
                    {
                        var test2 = resourceManager.ITMResourceEntries.FirstOrDefault(x => x.FullName.EndsWith(itmFilename));
                        if (test2 != null)
                        {
                            test2.LoadITMFiles();
                            return;
                        }
                    }
                }
                else
                {
                    filename = lastTry;
                }
            }
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                reader.BaseStream.Seek(originOffset, SeekOrigin.Begin);
                this.Signature = new string(reader.ReadChars(4));
                this.Version = new string(reader.ReadChars(4));
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out var text);
                this.GeneralName = text?.Text;
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out text);
                this.IdentifiedName = text?.Text ?? GeneralName ?? "None";

                // Abilities
                reader.BaseStream.Seek(originOffset + 0x60, SeekOrigin.Begin);
                this.Enchantment = reader.ReadInt32();
                var offsetToAbilities = reader.ReadInt32();
                var countOfAbilities = reader.ReadInt16();

                this.Effects = new List<ItemEffectEntry>();
                for (int i = 0; i < countOfAbilities; ++i)
                {
                    var abilityOffset = originOffset + offsetToAbilities + i * (0x72);
                    reader.BaseStream.Seek(abilityOffset + 0x4, SeekOrigin.Begin);
                    var iconBAM = new String(reader.ReadChars(8)).Trim('\0');                    
                    this.Icon = resourceManager.GetBAMReader(iconBAM)?.Image;
                    reader.BaseStream.Seek(abilityOffset + 0x1E, SeekOrigin.Begin);
                    var abilityEffectsCount = reader.ReadInt16();

                    for (int j = 0; j < abilityEffectsCount; j++)
                    {
                        Effects.Add(new ItemEffectEntry(reader, abilityOffset + 0x38 + j * 0x30));
                    }
                }
            }
        }
        public string Signature { get; private set; }
        public string Version { get; private set; }
        public string GeneralName { get; private set; }
        public string IdentifiedName { get; private set; }

        public List<ItemEffectEntry> Effects { get; }
        public int Enchantment { get; }
        public Bitmap Icon { get; }

        public static List<Effect> ExcludedItemEffectOpcodes => new[] {new List<Effect> {
            Effect.Creature_RGB_color_fade,
            Effect.Spell_Effect_Play_Sound_Effect,
            Effect.Colour_Glow_by_RGB_Brief,
            Effect.Colour_Change_by_RGB,
            Effect.Colour_Glow_Pulse,
            Effect.Colour_Very_Bright_by_RGB,
            Effect.Colour_Set_Character_colours_by_Palette,
            Effect.Colour_Strong_or_Dark_by_RGB,  
            Effect.Summon_Remove_Creature
        }, 
            // all the graphics
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Graphics")),
            // set stats
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Stat_")),
            // texts
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Text_")),
            // Cures
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Cure_")),
            // States
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("State_")),
        }.SelectMany(o => o).Cast<Effect>().ToList();
    }
}