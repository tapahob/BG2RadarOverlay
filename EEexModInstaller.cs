using System;
using System.IO;
using System.Reflection;

namespace BGOverlay
{
    public enum EEexModStatus
    {
        /// <summary>No game detected yet, so there is nowhere to install to.</summary>
        NoGameFolder,

        /// <summary>Neither file is in the override folder, or only one of the two is.</summary>
        NotInstalled,

        /// <summary>Both files are there, but at least one differs from the pair shipped here.</summary>
        Outdated,

        Installed
    }

    /// <summary>
    /// Puts the game-side half of the spawn bridge - see <see cref="GameSpawnBridge"/> and
    /// EEexMod/M_BG2RDR.lua - into the detected game's override folder, so a streamer setting the
    /// Twitch summons up doesn't have to go find two files and know where they belong.
    ///
    /// The files are carried as embedded resources rather than shipped next to the .exe. A
    /// release is a self-contained single-file publish, where "next to the .exe" is the host's
    /// temp extraction directory - it exists only while the app runs, and isn't where the user
    /// put anything. Reading them out of the assembly means the button works wherever the .exe
    /// was dropped, with no second folder to keep alongside it.
    ///
    /// This installs the bridge, not EEex. EEex is a separate mod with its own installer, and
    /// without it M_BG2RDR.lua errors out on load - the UI says so next to the button.
    /// </summary>
    public static class EEexModInstaller
    {
        private const string ResourcePrefix = "BGOverlay.EEexMod.";

        /// <summary>
        /// Both halves of the game-side bridge. The .lua performs the spawn; the .menu is what
        /// gives it a per-frame tick to perform it from, so installing either alone leaves a mod
        /// that loads and then never runs. They are always written as a pair for that reason.
        /// </summary>
        public static readonly string[] FileNames = { "M_BG2RDR.lua", "BG2RDR.menu" };

        /// <summary>Where the files go, or null while no game has been detected.</summary>
        public static string OverrideFolder
        {
            get
            {
                var gameFolder = Configuration.GameFolder;
                return string.IsNullOrEmpty(gameFolder)
                    ? null
                    : Path.Combine(gameFolder, "override");
            }
        }

        /// <summary>
        /// Whether EEex itself is installed in the detected game - the prerequisite this class
        /// does *not* provide. Without it M_BG2RDR.lua errors out the moment the game loads it,
        /// so every summon silently does nothing.
        ///
        /// Two markers, either of which is enough. A WeiDU install of EEex puts its Lua into
        /// override (M___EEex.lua on the installs seen so far, though the underscores have moved
        /// between versions, hence the pattern) and its loader next to the .exe. Deliberately
        /// generous: a false "not installed" would block a working setup, which is worse than
        /// letting a broken one through to the error it was always going to hit.
        ///
        /// Unknown - no game detected yet - reads as installed, for the same reason.
        /// </summary>
        public static bool IsEEexInstalled()
        {
            var gameFolder = Configuration.GameFolder;
            if (string.IsNullOrEmpty(gameFolder))
                return true;

            try
            {
                if (File.Exists(Path.Combine(gameFolder, "EEex.dll")))
                    return true;

                var overrideFolder = Path.Combine(gameFolder, "override");
                if (Directory.Exists(overrideFolder)
                    && Directory.GetFiles(overrideFolder, "M*EEex*.lua").Length > 0)
                    return true;
            }
            catch (Exception ex)
            {
                // An unreadable game folder is not evidence of anything.
                Logger.Error("Could not check whether EEex is installed", ex);
                return true;
            }

            return false;
        }

        public static EEexModStatus GetStatus()
        {
            var folder = OverrideFolder;
            if (folder == null)
                return EEexModStatus.NoGameFolder;

            var anyPresent = false;
            var allCurrent = true;

            foreach (var fileName in FileNames)
            {
                var path = Path.Combine(folder, fileName);
                if (!File.Exists(path))
                {
                    allCurrent = false;
                    continue;
                }

                anyPresent = true;

                try
                {
                    if (!sameText(File.ReadAllBytes(path), readResource(fileName)))
                        allCurrent = false;
                }
                catch (Exception ex)
                {
                    // An unreadable file is not a reason to claim the mod is fine.
                    Logger.Error($"Could not read installed mod file '{path}'", ex);
                    allCurrent = false;
                }
            }

            if (!anyPresent)
                return EEexModStatus.NotInstalled;

            return allCurrent ? EEexModStatus.Installed : EEexModStatus.Outdated;
        }

        /// <summary>
        /// Writes both files into the override folder, creating it if the game has never had one.
        /// Overwrites without asking: the button says install, and these two files are ours - a
        /// streamer who edited them by hand is not who this is for.
        /// </summary>
        public static bool TryInstall(out string error)
        {
            error = null;

            var folder = OverrideFolder;
            if (folder == null)
            {
                error = RadarLocalization.Get("Str_TwitchModNoGame");
                return false;
            }

            try
            {
                Directory.CreateDirectory(folder);

                foreach (var fileName in FileNames)
                    File.WriteAllBytes(Path.Combine(folder, fileName), readResource(fileName));

                Logger.Info($"Installed the EEex spawn bridge into '{folder}'");
                return true;
            }
            catch (Exception ex)
            {
                // Most likely a read-only game folder, or the game holding a file open. Hand the
                // reason back rather than a bare failure - it is the difference between "run this
                // as administrator" and "close the game first".
                Logger.Error("Could not install the EEex spawn bridge", ex);
                error = ex.Message;
                return false;
            }
        }

        private static byte[] readResource(string fileName)
        {
            var resourceName = ResourcePrefix + fileName;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing from the build.");

                var buffer = new byte[stream.Length];
                var read = 0;
                while (read < buffer.Length)
                {
                    var justRead = stream.Read(buffer, read, buffer.Length - read);
                    if (justRead <= 0)
                        break;
                    read += justRead;
                }
                return buffer;
            }
        }

        /// <summary>
        /// Compares two text files ignoring line endings, so a copy that only differs by CRLF vs
        /// LF - which is what a checkout on another platform, or an editor saving the file, will
        /// produce - doesn't read as out of date.
        /// </summary>
        private static bool sameText(byte[] a, byte[] b)
        {
            int i = 0, j = 0;

            while (i < a.Length && j < b.Length)
            {
                if (a[i] == '\r') { i++; continue; }
                if (b[j] == '\r') { j++; continue; }
                if (a[i] != b[j])
                    return false;
                i++;
                j++;
            }

            while (i < a.Length && a[i] == '\r') i++;
            while (j < b.Length && b[j] == '\r') j++;

            return i == a.Length && j == b.Length;
        }
    }
}
