using System.Drawing;

namespace BGOverlay
{
    /// <summary>
    /// A single inventory item shown in the EnemyControl "Pockets" section - one that can
    /// actually be stolen via pickpocket (CREReader only adds entries whose Unstealable flag
    /// is not set). Icon is null when the item's resource has none - the UI should simply omit
    /// the icon slot for a null one rather than showing a placeholder.
    /// </summary>
    public class PocketItemEntry
    {
        public string Name { get; set; }
        public Bitmap Icon { get; set; }
        // Localized, comma-joined flag words (e.g. "Identified, Stealable") - shown right-aligned
        // next to the name, by analogy with MainWindow's enemy list Name/HP layout.
        public string FlagsText { get; set; }
        public ITMReader ITMReader {get;set; }
        public int Count { get; set; }
    }
}
