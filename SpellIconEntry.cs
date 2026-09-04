using System.Collections.Generic;
using System.Drawing;

namespace BGOverlay
{
    /// <summary>
    /// A single named, optionally-iconed spell within a "Immune to spells" Protections entry.
    /// Icon is the game resource's icon Bitmap when one was found, or null - the UI should
    /// simply omit the icon slot for a null one rather than showing a placeholder.
    /// </summary>
    public class SpellIconEntry
    {
        public string Name { get; set; }
        public Bitmap Icon { get; set; }
        // Set by the list builder once the final (sorted) order is known, so the UI can print
        // a trailing comma after every entry except the last one.
        public bool IsLast { get; set; }
    }

    /// <summary>
    /// One "Immune to spells: ..." line in <see cref="BGEntity.Protections"/>, kept as a small
    /// structured object (a label plus a list of <see cref="SpellIconEntry"/>) instead of one
    /// big comma-joined string like the rest of Protections, so the UI can show each spell's
    /// icon to the left of its name.
    /// </summary>
    public class SpellImmunityLine
    {
        public string Label { get; set; }
        public List<SpellIconEntry> Spells { get; set; }
    }
}
