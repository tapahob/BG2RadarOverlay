using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

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
                        var test2 = resourceManager.ITMResourceEntries.FirstOrDefault(x => x.FullName == itmFilename)
                            ?? resourceManager.ITMResourceEntries.FirstOrDefault(x => x.FullName.EndsWith(itmFilename));
                        
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
            try
            {
                using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
                {
                    reader.BaseStream.Seek(originOffset, SeekOrigin.Begin);
                    this.Signature = new string(reader.ReadChars(4));
                    this.Version = new string(reader.ReadChars(4));

                    var generalStrRef = reader.ReadInt32();
                    resourceManager.StringRefs.TryGetValue(generalStrRef, out var text);
                    this.GeneralName = text?.Text;
                    var identifiedStrRef = reader.ReadInt32();
                    resourceManager.StringRefs.TryGetValue(identifiedStrRef, out text);
                    this.IdentifiedName = text?.Text ?? GeneralName ?? "None";
                    var trash = new string(reader.ReadChars(8));
                    this.Flags = reader.ReadInt32();
                    reader.BaseStream.Seek(originOffset + 0x60, SeekOrigin.Begin);

                    // Abilities
                    reader.BaseStream.Seek(originOffset + 0x60, SeekOrigin.Begin);
                    this.Enchantment = reader.ReadInt32();
                    var offsetToAbilities = reader.ReadInt32();
                    var countOfAbilities = reader.ReadInt16();
                    var offsetToFeatureBlocks = reader.ReadInt32();

                    // Fiddling with damage types
                    if (countOfAbilities > 0)
                    {
                        var abilityOffset = originOffset + offsetToAbilities;
                        reader.BaseStream.Seek(abilityOffset + 0x1C, SeekOrigin.Begin);
                        var dmgTypeValue = reader.ReadInt16();
                        this.DamageType = (DamageType)(dmgTypeValue > 9 ? 0 : dmgTypeValue);
                    }
                    
                    this.Effects = new List<ItemEffectEntry>();
                    for (int i = 0; i < countOfAbilities; ++i)
                    {
                        var abilityOffset = originOffset + offsetToAbilities + i * 56;
                        reader.BaseStream.Seek(abilityOffset, SeekOrigin.Begin);
                        var abilityType = reader.ReadByte();
                        if (abilityType != 1)
                            continue;
                        reader.BaseStream.Seek(abilityOffset + 0x4, SeekOrigin.Begin);
                        var iconBAM = new String(reader.ReadChars(8)).Trim('\0');
                        this.Icon = this.Icon ?? resourceManager.GetBAMReader(iconBAM)?.Image;
                        reader.BaseStream.Seek(abilityOffset + 0x1E, SeekOrigin.Begin);
                        var abilityEffectsCount = reader.ReadInt16();
                        var abilityEffectsIndex = reader.ReadInt16();
                        reader.BaseStream.Seek(originOffset + offsetToFeatureBlocks, SeekOrigin.Begin);
                        for (int j = 0; j < abilityEffectsCount; j++)
                        {
                            try
                            {
                                Effects.Add(new ItemEffectEntry(reader, originOffset + offsetToFeatureBlocks + ((abilityEffectsIndex + j) * 0x30)));
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"ITM Reader Error while reading Item Effects", ex);
                            }
                            
                        }
                    }
                }
            } catch (Exception ex)
            {
                Logger.Error($"ITMReader error: {itmFilename}", ex);
            }
        }

        public DamageType DamageType { get; private set; }

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
            Effect.Summon_Remove_Creature,
            Effect.HP_Damage
        }, 
            // all the graphics
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Graphics")),
            //// set stats
            //Enum.GetValues(typeof(Effect)).Cast<Effect>()
            //.Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Stat_")),
            // texts
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Text_")),
            // Cures
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("Cure_")),
            // States
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().StartsWith("State_")),
            // Protections
            Enum.GetValues(typeof(Effect)).Cast<Effect>()
            .Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().Contains("Protection")),
            //// Removals
            //Enum.GetValues(typeof(Effect)).Cast<Effect>()
            //.Where(x => Enum.Parse(typeof(Effect), x.ToString()).ToString().Contains("Removal")),
        }.SelectMany(o => o).Cast<Effect>().ToList();

        public int Flags { get; private set; }
    }
}