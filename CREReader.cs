using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace BGOverlay
{
    // reference: https://gibberlings3.github.io/iesdp/file_formats/ie_formats/cre_v1.htm
    public class CREReader
    {
        public static Dictionary<string, CREReader> Cache = new Dictionary<string, CREReader>();
        public static CREReader get(String creFilename)
        {
            creFilename = creFilename.ToUpper();
            if (creFilename.StartsWith("HARBASE"))
            {
                creFilename = "C" + creFilename;
            }
            CREReader reader; 
            if (!Cache.TryGetValue(creFilename, out reader))
            {
                try
                {
                    reader = new CREReader(creFilename);
                    Cache.Add(creFilename, reader);
                } catch (ArgumentException ex)
                {
                    //Console.WriteLine(ex.Message);
                    reader = null;
                }
                
            }
            return reader;
        }
        public static string gameDirectory = Configuration.GameFolder;
        public CREReader(String creFilename = "DUERGAR.CRE", int originOffset = 0, string biffArchivePath = "")
        {
            var filename = creFilename;
            var overrideCreFilename = $"{gameDirectory}\\override\\{creFilename}";
            if (File.Exists(overrideCreFilename))
            {
                originOffset = 0;
                biffArchivePath = "";
                filename = overrideCreFilename;
            } 
            else
            {
                filename = biffArchivePath;
            }
            using (BinaryReader reader = new BinaryReader(File.OpenRead(filename)))
            {
                this.Signature = new string(reader.ReadChars(4));
                this.Version   = new string(reader.ReadChars(4));
                this.LongName  = reader.ReadInt32();
                this.ShortName = reader.ReadInt32();

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
                this.KitInformation = reader.ReadInt32();
                /**
                 * Kit information:
                    NONE                0x00000000
                    KIT_BARBARIAN       0x00004000
                    KIT_TRUECLASS       0x40000000
                    KIT_BERSERKER       0x40010000
                    KIT_WIZARDSLAYER    0x40020000
                    KIT_KENSAI          0x40030000
                    KIT_CAVALIER        0x40040000
                    KIT_INQUISITOR      0x40050000
                    KIT_UNDEADHUNTER    0x40060000
                    KIT_ARCHER          0x40070000
                    KIT_STALKER         0x40080000
                    KIT_BEASTMASTER     0x40090000
                    KIT_ASSASSIN        0x400A0000
                    KIT_BOUNTYHUNTER    0x400B0000
                    KIT_SWASHBUCKLER    0x400C0000
                    KIT_BLADE           0x400D0000
                    KIT_JESTER          0x400E0000
                    KIT_SKALD           0x400F0000
                    KIT_TOTEMIC         0x40100000
                    KIT_SHAPESHIFTER    0x40110000
                    KIT_AVENGER         0x40120000
                    KIT_GODTALOS        0x40130000
                    KIT_GODHELM         0x40140000
                    KIT_GODLATHANDER    0x40150000
                    ABJURER             0x00400000
                    CONJURER            0x00800000
                    DIVINER             0x01000000
                    ENCHANTER           0x02000000
                    ILLUSIONIST         0x04000000
                    INVOKER             0x08000000
                    NECROMANCER         0x10000000
                    TRANSMUTER          0x20000000

                    NB.: The values of this offset are written in big endian style.
                 */

                reader.BaseStream.Seek(originOffset + 0x0271, SeekOrigin.Begin);
                this.General = reader.ReadByte();
                this.Race = reader.ReadByte();
                this.Class = reader.ReadByte();

                reader.BaseStream.Seek(originOffset + 0x027b, SeekOrigin.Begin);
                this.Alignment = reader.ReadByte();
            }

        }

        public int ShortName { get; private set; }
        public int CreatureFlags { get; private set; }
        public int XPGained { get; private set; }
        public short MaximumHP { get; private set; }
        public string Signature { get; private set; }
        public string Version { get; private set; }
        public int LongName { get; private set; }
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
        public int KitInformation { get; }
        public byte General { get; }
        public byte Race { get; }
        public byte Class { get; }
        public byte Alignment { get; }
    }

    internal struct NewStruct
    {
        public object Item1;
        public object Item2;

        public NewStruct(object item1, object item2)
        {
            Item1 = item1;
            Item2 = item2;
        }

        public override bool Equals(object obj)
        {
            return obj is NewStruct other &&
                   EqualityComparer<object>.Default.Equals(Item1, other.Item1) &&
                   EqualityComparer<object>.Default.Equals(Item2, other.Item2);
        }

        public override int GetHashCode()
        {
            int hashCode = -1030903623;
            hashCode = hashCode * -1521134295 + EqualityComparer<object>.Default.GetHashCode(Item1);
            hashCode = hashCode * -1521134295 + EqualityComparer<object>.Default.GetHashCode(Item2);
            return hashCode;
        }

        public void Deconstruct(out object item1, out object item2)
        {
            item1 = Item1;
            item2 = Item2;
        }

        public static implicit operator (object, object)(NewStruct value)
        {
            return (value.Item1, value.Item2);
        }

        public static implicit operator NewStruct((object, object) value)
        {
            return new NewStruct(value.Item1, value.Item2);
        }
    }
}
