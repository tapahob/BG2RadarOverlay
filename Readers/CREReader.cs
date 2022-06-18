using BGOverlay.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BGOverlay
{
    // reference: https://gibberlings3.github.io/iesdp/file_formats/ie_formats/cre_v1.htm
    public class CREReader
    {
        public static string gameDirectory = Configuration.GameFolder;
        private ResourceManager resourceManager;

        public CREReader(ResourceManager resourceManager, String creFilename = "DUERGAR.CRE", int originOffset = 0, string biffArchivePath = "")
        {
            this.resourceManager = resourceManager;
            var filename         = creFilename;
            var overrideCreFilename = $"{gameDirectory}\\override\\{creFilename}";
            if (File.Exists(overrideCreFilename))
            {
                originOffset    = 0;
                biffArchivePath = "";
                filename        = overrideCreFilename;
            } 
            else
            {   
                var lastTry = Directory.Exists($"{gameDirectory}\\override") 
                    ? Directory.GetFiles($"{gameDirectory}\\override").Select(x=>x.ToUpper()).FirstOrDefault(x => x.EndsWith(creFilename))
                    : null;
                if (lastTry == null)
                {
                    filename = biffArchivePath;
                    if (biffArchivePath.Equals(""))
                    {
                        var test2 = resourceManager.CREResourceEntries.FirstOrDefault(x => x.FullName.EndsWith(creFilename));
                        if (test2 != null)
                        {
                            test2.LoadCREFiles();
                            return;
                        }  
                    }
                } else
                {
                    filename = lastTry;
                }

            }
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                reader.BaseStream.Seek(originOffset, SeekOrigin.Begin);
                this.Signature = new string(reader.ReadChars(4));
                this.Version   = new string(reader.ReadChars(4));
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out var text);
                this.LongName = text?.Text;
                resourceManager.StringRefs.TryGetValue(reader.ReadInt32(), out text);
                this.ShortName = text?.Text;

                this.CreatureFlags = reader.ReadInt32();
                /*
                 * Creature flags
                    bit 0 Show longname in tooltip
                    bit 1: No corpse
                    bit 2: Keep corpse
                    bit 3: Original class was Fighter
                    bit 4: Original class was Mage
                    bit 5: Original class was Cleric
                    bit 6: Original class was Thief
                    bit 7: Original class was Druid
                    bit 8: Original class was Ranger
                    bit 9: Fallen Paladin
                    bit 10: Fallen Ranger
                    bit 11: Exportable
                    bit 12: Hide injury status in tooltip
                    bit 13: Quest critical / affected by alternative damage
                    bit 14 ⟶ Moving between areas
                    See opcode #186
                    bit 15: Been in Party
                    bit 16: Restore item in hand
                    bit 17: Un-sets bit 16
                    bit 18 - 19 Unused
                    bit 20: Prevent exploding death (BGEE)
                    bit 21: Unused
                    bit 22: Don't apply nightmare mode modifiers (BGEE)
                    bit 23: No tooltip (BGEE)
                    bit 24: Related to random walk (ea)
                    bit 25: Related to random walk (general)
                    bit 26: Related to random walk (race)
                    bit 27: Related to random walk (class)
                    bit 28: Related to random walk (specific)
                    bit 29: Related to random walk (gender)
                    bit 30: Related to random walk (alignment)
                    bit 31: Un-interruptable (memory only)
                    A multiclass char is indicated by the absence of any of the "original class" flags being set.
                 */

                this.XPGained = reader.ReadInt32();

                reader.BaseStream.Seek(originOffset + 0x0026, SeekOrigin.Begin);
                this.MaximumHP = reader.ReadInt16();

                reader.BaseStream.Seek(originOffset + 0x0033, SeekOrigin.Begin);
                this.EffStructureVersion = reader.ReadByte();
                this.SmallPortrait       = reader.ReadChars(8);
                this.LargePortrait       = reader.ReadChars(8);

                reader.BaseStream.Seek(originOffset + 0x0046, SeekOrigin.Begin);
                this.ArmorClassNatural   = reader.ReadInt16();
                this.ArmorClassEffective = reader.ReadInt16();
                this.ArmorClassCrushing  = reader.ReadInt16();
                this.ArmorClassMissile   = reader.ReadInt16();
                this.ArmorClassPiercing  = reader.ReadInt16();
                this.ArmorClassSlashing  = reader.ReadInt16();
                this.THAC0               = reader.ReadByte();
                this.NumberOfAttacks     = reader.ReadByte();
                this.SaveVersusDeath     = reader.ReadByte();
                this.SaveVersusWands     = reader.ReadByte();
                this.SaveVersusPolymorph = reader.ReadByte();
                this.SaveVersusBreath    = reader.ReadByte();
                this.SaveVersusSpells    = reader.ReadByte();
                this.ResistFire          = reader.ReadByte();
                this.ResistCold          = reader.ReadByte();
                this.ResistElectricity   = reader.ReadByte();
                this.ResistAcid          = reader.ReadByte();
                this.ResistMagic         = reader.ReadByte();
                this.ResistMagicFire     = reader.ReadByte();
                this.ResistMagicCold     = reader.ReadByte();
                this.ResistSlashing      = reader.ReadByte();
                this.ResistCrushing      = reader.ReadByte();
                this.ResistPiercing      = reader.ReadByte();
                this.ResistMissile       = reader.ReadByte();

                reader.BaseStream.Seek(originOffset + 0x0234, SeekOrigin.Begin);
                this.Class1Level          = reader.ReadByte();
                this.Class2Level          = reader.ReadByte();
                this.Class3Level          = reader.ReadByte();
                this.Gender               = reader.ReadByte();
                this.Strength             = reader.ReadByte();
                this.StrengthPercentBonus = reader.ReadByte();
                this.Intelligence         = reader.ReadByte();
                this.Wisdom               = reader.ReadByte();
                this.Dexterity            = reader.ReadByte();
                this.Constitution         = reader.ReadByte();
                this.Charisma             = reader.ReadByte();

                reader.BaseStream.Seek(originOffset + 0x0244, SeekOrigin.Begin);
                this.KitInformation = (KIT)reader.ReadInt32();
                                
                reader.BaseStream.Seek(originOffset + 0x0270, SeekOrigin.Begin);
                this.EnemyAlly = reader.ReadByte();
                this.General   = reader.ReadByte();
                this.Race      = (RACE)reader.ReadByte();
                this.Class     = (CLASS)reader.ReadByte();

                reader.BaseStream.Seek(originOffset + 0x027b, SeekOrigin.Begin);
                this.SetAlignment((ALIGNMENT)reader.ReadByte());

                // Items
                reader.BaseStream.Seek(originOffset + 0x02bc, SeekOrigin.Begin);
                var offsetToItems = reader.ReadInt32();
                int countOfItems = reader.ReadInt32();
                this.Items = new List<ITMReader>();
                for (int i = 0; i < countOfItems; ++i)
                {
                    reader.BaseStream.Seek(originOffset + offsetToItems + i * (0x14), SeekOrigin.Begin);
                    string itmFileName = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8));
                    this.resourceManager.log(itmFileName);
//                    ITMReader itmReader = this.resourceManager.GetITMReader(itmFileName);
//                    if (itmReader != null)
//                    {
//                        //Items.Add(itmReader);
//                    }
                }

                // Effects
                reader.BaseStream.Seek(originOffset + 0x02c4, SeekOrigin.Begin);
                var offsetToEffects = reader.ReadInt32();
                var countOfEffects  = reader.ReadInt32();

                reader.BaseStream.Seek(originOffset + offsetToEffects, SeekOrigin.Begin);
                bool isV2Version = this.EffStructureVersion == 1;
                this.Effects = new List<EffectEntry>();
                for (int i=0; i<countOfEffects; ++i)
                {
                    var begining = originOffset + offsetToEffects + i * (0x108);
                    if (isV2Version)
                    {
                        Effects.Add(new EffectEntry(reader, begining));
                    }
                }
            }

        }

        public enum ALIGNMENT
        {
            NONE            = 0x00,
            LAWFUL_GOOD     = 0x11,
            LAWFUL_NEUTRAL  = 0x12,
            LAWFUL_EVIL     = 0x13,
            NEUTRAL_GOOD    = 0x21,
            NEUTRAL         = 0x22,
            NEUTRAL_EVIL    = 0x23,
            CHAOTIC_GOOD    = 0x31,
            CHAOTIC_NEUTRAL = 0x32,
            CHAOTIC_EVIL    = 0x33,
            MASK_GOOD       = 0x01,
            MASK_GENEUTRAL  = 0x02,
            MASK_EVIL       = 0x03,
            MASK_LAWFUL     = 0x10,
            MASK_LCNEUTRAL  = 0x20,
            MASK_CHAOTIC    = 0x30,
        }

        public enum CLASS
        {
            ERROR                = 0,
            MAGE                 = 1,
            FIGHTER              = 2,
            CLERIC               = 3,
            THIEF                = 4,
            BARD                 = 5,
            PALADIN              = 6,
            FIGHTER_MAGE         = 7,
            FIGHTER_CLERIC       = 8,
            FIGHTER_THIEF        = 9,
            FIGHTER_MAGE_THIEF   = 10,
            DRUID                = 11,
            RANGER               = 12,
            MAGE_THIEF           = 13,
            CLERIC_MAGE          = 14,
            CLERIC_THIEF         = 15,
            FIGHTER_DRUID        = 16,
            FIGHTER_MAGE_CLERIC  = 17,
            CLERIC_RANGER        = 18,
            SORCERER             = 19,
            MONK                 = 20,
            SHAMAN               = 21,
            ANKHEG               = 101,
            BASILISK             = 102,
            BASILISK_GREATER     = 103,
            BEAR_BLACK           = 104,
            BEAR_BROWN           = 105,
            BEAR_CAVE            = 106,
            BEAR_POLAR           = 107,
            CARRIONCRAWLER       = 108,
            DOG_WILD             = 109,
            DOG_WAR              = 110,
            DOPPLEGANGER         = 111,
            DOPPLEGANGER_GREATER = 112,
            DRIZZT               = 113,
            ELMINSTER            = 114,
            ETTERCAP             = 115,
            GHOUL                = 116,
            GHOUL_REVEANT        = 117,
            GHOUL_GHAST          = 118,
            GIBBERLING           = 119,
            GNOLL                = 120,
            HOBGOBLIN            = 121,
            KOBOLD               = 122,
            KOBOLD_TASLOI        = 123,
            KOBOLD_XVART         = 124,
            OGRE                 = 125,
            OGRE_MAGE            = 126,
            OGRE_HALFOGRE        = 127,
            OGRE_OGRILLON        = 128,
            SAREVOK              = 129,
            FAIRY_SIRINE         = 130,
            FAIRY_DRYAD          = 131,
            FAIRY_NEREID         = 132,
            FAIRY_NYMPH          = 133,
            SKELETON             = 134,
            SKELETON_WARRIOR     = 135,
            SKELETON_BANEGUARD   = 136,
            SPIDER_GIANT         = 137,
            SPIDER_HUGE          = 138,
            SPIDER_PHASE         = 139,
            SPIDER_SWORD         = 140,
            SPIDER_WRAITH        = 141,
            VOLO                 = 142,
            WOLF                 = 143,
            WOLF_WORG            = 144,
            WOLF_DIRE            = 145,
            WOLF_WINTER          = 146,
            WOLF_VAMPIRIC        = 147,
            WOLF_DREAD           = 148,
            WYVERN               = 149,
            OLIVE_SLIME          = 150,
            MUSTARD_JELLY        = 151,
            OCRE_JELLY           = 152,
            GREY_OOZE            = 153,
            GREEN_SLIME          = 154,
            INNOCENT             = 155,
            FLAMING_FIST         = 156,
            WEREWOLF             = 157,
            WOLFWERE             = 158,
            DEATHKNIGHT          = 159,
            TANARI               = 160,
            BEHOLDER             = 161,
            MIND_FLAYER          = 162,
            VAMPIRE              = 163,
            VAMPYRE              = 164,
            OTYUGH               = 165,
            RAKSHASA             = 166,
            TROLL                = 167,
            UMBERHULK            = 168,
            SAHUAGIN             = 169,
            SHADOW               = 170,
            SPECTRE              = 171,
            WRAITH               = 172,
            KUO_TOA              = 173,
            MIST                 = 174,
            CAT                  = 175,
            DUERGAR              = 176,
            MEPHIT               = 177,
            MIMIC                = 178,
            IMP                  = 179,
            GIANT                = 180,
            ORC                  = 181,
            GOLEM_IRON           = 182,
            GOLEM_FLESH          = 183,
            GOLEM_STONE          = 184,
            GOLEM_CLAY           = 185,
            ELEMENTAL_AIR        = 186,
            ELEMENTAL_FIRE       = 187,
            ELEMENTAL_EARTH      = 188,
            SPIDER_CENTEOL       = 189,
            RED_DRAGON           = 190,
            SHADOW_DRAGON        = 191,
            SILVER_DRAGON        = 192,
            GENIE_DJINNI         = 193,
            GENIE_DAO            = 194,
            GENIE_EFREETI        = 195,
            GENIE_NOBLE_DJINNI   = 196,
            GENIE_NOBLE_EFREETI  = 197,
            ZOMBIE_NORMAL        = 198,
            FOOD_CREATURE        = 199,
            HUNTER_CREATURE      = 200,
            LONG_SWORD           = 201,
            LONG_BOW             = 202,
            MAGE_ALL             = 202,
            FIGHTER_ALL          = 203,
            CLERIC_ALL           = 204,
            THIEF_ALL            = 205,
            BARD_ALL             = 206,
            PALADIN_ALL          = 207,
            DRUID_ALL            = 208,
            RANGER_ALL           = 209,
            WIZARD_EYE           = 210,
            CANDLEKEEP_WATCHER   = 211,
            AMNISH_SOLDIER       = 212,
            TOWN_GUARD           = 213,
            ELEMENTAL_WATER      = 219,
            GREEN_DRAGON         = 220,
            SOD_TMP              = 221,
            SPECTRAL_TROLL       = 222,
            WIGHT                = 223,
            NO_CLASS             = 255
        }

        public enum KIT
        {
            NONE         = 0x00000000,
            BARBARIAN    = 0x00004000,
            TRUECLASS    = 0x40000000,
            BERSERKER    = 0x40010000,
            WIZARDSLAYER = 0x40020000,
            KENSAI       = 0x40030000,
            CAVALIER     = 0x40040000,
            INQUISITOR   = 0x40050000,
            UNDEADHUNTER = 0x40060000,
            ARCHER       = 0x40070000,
            STALKER      = 0x40080000,
            BEASTMASTER  = 0x40090000,
            ASSASSIN     = 0x400A0000,
            BOUNTYHUNTER = 0x400B0000,
            SWASHBUCKLER = 0x400C0000,
            BLADE        = 0x400D0000,
            JESTER       = 0x400E0000,
            SKALD        = 0x400F0000,
            TOTEMIC      = 0x40100000,
            SHAPESHIFTER = 0x40110000,
            AVENGER      = 0x40120000,
            GODTALOS     = 0x40130000,
            GODHELM      = 0x40140000,
            GODLATHANDER = 0x40150000,
            ABJURER      = 0x00400000,
            CONJURER     = 0x00800000,
            DIVINER      = 0x01000000,
            ENCHANTER    = 0x02000000,
            ILLUSIONIST  = 0x04000000,
            INVOKER      = 0x08000000,
            NECROMANCER  = 0x10000000,
            TRANSMUTER   = 0x20000000
        }

        public enum RACE
        {
            HUMAN           = 1,
            ELF             = 2,
            HALF_ELF        = 3,
            DWARF           = 4,
            HALFLING        = 5,
            GNOME           = 6,
            HALFORC         = 7,
            ANKHEG          = 101,
            BASILISK        = 102,
            BEAR            = 103,
            CARRIONCRAWLER  = 104,
            DOG             = 105,
            DOPPLEGANGER    = 106,
            ETTERCAP        = 107,
            GHOUL           = 108,
            GIBBERLING      = 109,
            GNOLL           = 110,
            HOBGOBLIN       = 111,
            KOBOLD          = 112,
            OGRE            = 113,
            SKELETON        = 115,
            SPIDER          = 116,
            WOLF            = 117,
            WYVERN          = 118,
            SLIME           = 119,
            FAIRY           = 120,
            DEMONIC         = 121,
            LYCANTHROPE     = 122,
            BEHOLDER        = 123,
            MIND_FLAYER     = 124,
            VAMPIRE         = 125,
            VAMPYRE         = 126,
            OTYUGH          = 127,
            RAKSHASA        = 128,
            TROLL           = 129,
            UMBERHULK       = 130,
            SAHUAGIN        = 131,
            SHADOW          = 132,
            SPECTRE         = 133,
            WRAITH          = 134,
            KUO_TOA         = 135,
            MIST            = 136,
            CAT             = 137,
            DUERGAR         = 138,
            MEPHIT          = 139,
            MIMIC           = 140,
            IMP             = 141,
            GIANT           = 142,
            ORC             = 143,
            GOLEM           = 144,
            ELEMENTAL       = 145,
            DRAGON          = 146,
            GENIE           = 147,
            ZOMBIE          = 148,
            STATUE          = 149,
            LICH            = 150,
            RABBIT          = 151,
            GITHYANKI       = 152,
            TIEFLING        = 153,
            YUANTI          = 154,
            DEMILICH        = 155,
            SOLAR           = 156,
            ANTISOLAR       = 157,
            PLANATAR        = 158,
            DARKPLANATAR    = 159,
            BEETLE          = 160,
            GOBLIN          = 161,
            LIZARDMAN       = 162,
            MYCONID         = 164,
            BUGBEAR         = 165,
            FEYR            = 166,
            HOOK_HORROR     = 167,
            SHRIEKER        = 168,
            SALAMANDER      = 169,
            BIRD            = 170,
            MINOTAUR        = 171,
            DRIDER          = 172,
            SIMULACRUM      = 173,
            HARPY           = 174,
            SPECTRAL_UNDEAD = 175,
            SHAMBLING_MOUND = 176,
            CHIMERA         = 177,
            HALF_DRAGON     = 178,
            YETI            = 179,
            KEG             = 180,
            WILL_O_WISP     = 181,
            MAMMAL          = 182,
            REPTILE         = 183,
            TREANT          = 184,
            AASIMAR         = 185,
            ETTIN           = 199,
            SWORD           = 201,
            BOW             = 202,
            XBOW            = 203,
            STAFF           = 204,
            SLING           = 205,
            MACE            = 206,
            DAGGER          = 207,
            SPEAR           = 208,
            FIST            = 209,
            HAMMER          = 210,
            MORNINGSTAR     = 211,
            ROBES           = 212,
            LEATHER         = 213,
            CHAIN           = 214,
            PLATE           = 215,
            NO_RACE         = 255,
        }

       
        public string ShortName { get; private set; }
        public int CreatureFlags { get; private set; }
        public int XPGained { get; private set; }
        public short MaximumHP { get; private set; }
        public string Signature { get; private set; }
        public string Version { get; private set; }
        public string LongName { get; private set; }
        public byte EffStructureVersion { get; }
        public char[] SmallPortrait { get; }
        public char[] LargePortrait { get; }
        public short ArmorClassNatural { get; }
        public short ArmorClassEffective { get; }
        public short ArmorClassCrushing { get; }
        public short ArmorClassMissile { get; }
        public short ArmorClassPiercing { get; }
        public short ArmorClassSlashing { get; }
        public byte THAC0 { get; }
        public byte NumberOfAttacks { get; }
        public byte SaveVersusDeath { get; }
        public byte SaveVersusWands { get; }
        public byte SaveVersusPolymorph { get; }
        public byte SaveVersusBreath { get; }
        public byte SaveVersusSpells { get; }
        public byte ResistFire { get; }
        public byte ResistCold { get; }
        public byte ResistElectricity { get; }
        public byte ResistAcid { get; }
        public byte ResistMagic { get; }
        public byte ResistMagicFire { get; }
        public byte ResistMagicCold { get; }
        public byte ResistSlashing { get; }
        public byte ResistCrushing { get; }
        public byte ResistPiercing { get; }
        public byte ResistMissile { get; }
        public byte Class1Level { get; }
        public byte Class2Level { get; }
        public byte Class3Level { get; }
        public byte Gender { get; }
        public byte Strength { get; }
        public byte StrengthPercentBonus { get; }
        public byte Intelligence { get; }
        public byte Wisdom { get; }
        public byte Dexterity { get; }
        public byte Constitution { get; }
        public byte Charisma { get; }
        public KIT KitInformation { get; }
        public byte EnemyAlly { get; }
        public byte General { get; }
        public RACE Race { get; }
        public CLASS Class { get; }

        private ALIGNMENT alignment;

        public string ShortAlignment
        {
            get
            {
                switch (alignment)
                {
                    case ALIGNMENT.CHAOTIC_EVIL:
                    case ALIGNMENT.LAWFUL_EVIL:
                    case ALIGNMENT.NEUTRAL_EVIL:
                        return "Evil";
                    case ALIGNMENT.CHAOTIC_GOOD:
                    case ALIGNMENT.LAWFUL_GOOD:
                    case ALIGNMENT.NEUTRAL_GOOD:
                        return "Good";
                    default: return "Neutral";
                }
            }
        }

        private void SetAlignment(ALIGNMENT value)
        {
            alignment = value;
        }

        public List<EffectEntry> Effects { get; }

        public List<ITMReader> Items { get; }
    }
}