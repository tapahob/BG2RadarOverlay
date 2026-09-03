using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WinApiBindings;

namespace BGOverlay
{
    public delegate void ProcessStateChangedHandler(string processName, int pid);

    public class ProcessHacker
    {
        public Process Proc;
        private IntPtr hProc;        
        private IntPtr moduleBase;

        public event ProcessStateChangedHandler ProcessDestroyed;
        public event ProcessStateChangedHandler ProcessFound;
        public event ProcessStateChangedHandler ProcessHooked;

        public List<BGEntity> entityList;

        public ObservableCollection<String> TextEntries = new ObservableCollection<string>();
        public ResourceManager ResourceManager { get; private set; }
        public List<BGEntity> NearestEnemies { get; private set; }

        private BGEntity main;

        private List<BGEntity> entityListTemp = new List<BGEntity>();
        private List<BGEntity> allEntities    = new List<BGEntity>();
        private static IntPtr entityListPtr   = IntPtr.Zero;
        public static string gameName = "Baldur";

        // Slot index -> already-constructed BGEntity, kept across ticks so an unchanged
        // creature only pays for a full pointer-chasing reconstruction once, and every
        // later tick just re-reads its volatile fields (position/HP/buffs-pointer targets).
        private readonly Dictionary<int, BGEntity> entityPool = new Dictionary<int, BGEntity>();
        private readonly HashSet<int> seenIndexes = new HashSet<int>();

        private const int SlotSize       = 16;
        private const int ScanChunkSlots = 4096; // 64KB/chunk - far fewer syscalls than one-per-slot, small enough to stay a plain gen0 allocation.

        public void MainLoop()
        {
            entityListTemp.Clear();
            allEntities.Clear();
            seenIndexes.Clear();
            if (Proc.HasExited)
            {
                NearestEnemies.Clear();
                TextEntries.Clear();
                entityPool.Clear();
                ProcessDestroyed?.Invoke(Proc.ProcessName, Proc.Id);
                this.Init();
            }

            var staticEntityList26= moduleBase + 0x68D438 + 0x18; // 2.6 entity array
            IntPtr staticEntityList27 = moduleBase + (gameName == "baldur" ? 0x68F910 : 0x695910); // 2.7 entity array

            var test = WinAPIBindings.FindDMAAddy(entityListPtr, new int[] { });
            var length = 65535;
            var marginOfError = 500;

            // First i = 32016
            int startOffset        = 2000 * 16;
            int endOffsetExclusive = length * 16 + marginOfError;
            int chunkBytes         = ScanChunkSlots * SlotSize;

            // This used to issue one ReadProcessMemory syscall per 16-byte slot (~63k
            // syscalls/tick just to check whether a slot is the 65535 "empty" sentinel).
            // Reading the same range in a handful of larger chunks and parsing the index
            // out of the local buffer reads the exact same bytes for a fraction of the
            // syscalls.
            for (int chunkStart = startOffset; chunkStart < endOffsetExclusive; chunkStart += chunkBytes)
            {
                int bytesToRead = Math.Min(chunkBytes, endOffsetExclusive - chunkStart);
                byte[] chunk;
                try
                {
                    chunk = WinAPIBindings.ReadBytes(test + chunkStart, bytesToRead);
                }
                catch (Exception ex)
                {
                    Logger.Error("Error reading actor list chunk!", ex);
                    continue;
                }

                for (int offset = 0; offset + 4 <= bytesToRead; offset += SlotSize)
                {
                    try
                    {
                        var index = BitConverter.ToInt32(chunk, offset);
                        if (index == 65535)
                            continue;

                        seenIndexes.Add(index);

                        var i = chunkStart + offset;

                        // The pooled template is never mutated - RefreshedCopy() hands back a
                        // brand new object each time (cheap: it only re-reads the volatile
                        // fields, the static ones are copied in memory) so entityListTemp
                        // always holds a fresh, private-to-this-tick snapshot, same as when
                        // every entity was fully reconstructed from scratch.
                        BGEntity newEntity = null;
                        if (entityPool.TryGetValue(index, out var template))
                        {
                            newEntity = template.RefreshedCopy();
                        }

                        if (newEntity == null)
                        {
                            var entityPtr = WinAPIBindings.FindDMAAddy(test + i + 0x8);
                            newEntity = new BGEntity(ResourceManager, entityPtr);
                            if (!newEntity.Loaded)
                                continue;
                            entityPool[index] = newEntity;
                        }

                        if (newEntity.Name2 == "<ERROR>" || newEntity.CurrentHP == 0)
                            continue;

                        allEntities.Add(newEntity);
                        if (Configuration.HidePartyMembers)
                        {
                            if (newEntity.EnemyAlly == 2)
                                continue;
                        }

                        if (Configuration.HideNeutrals)
                        {
                            if (newEntity.EnemyAlly == 128)
                                continue;
                        }

                        if (Configuration.HideAllies)
                        {
                            if (newEntity.EnemyAlly == 4)
                                continue;
                        }
                        newEntity.tag = index;
                        entityListTemp.Add(newEntity);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error during actor list scan!", ex);
                    }
                }
            }

            // Drop pooled entities whose slot didn't show up this pass (the creature died,
            // left the area, or the slot went back to the empty sentinel) so the pool tracks
            // only currently active entities instead of growing without bound.
            if (entityPool.Count > 0)
            {
                var stale = entityPool.Keys.Where(k => !seenIndexes.Contains(k)).ToList();
                foreach (var k in stale)
                    entityPool.Remove(k);
            }

            entityList = entityListTemp;

            if (!entityList.Any())
            {
                entityListPtr = entityListPtr == staticEntityList27 ? staticEntityList26 : staticEntityList27;
            }
            this.NearestEnemies = entityListTemp.Where(y => clip(y)).ToList();
            TextEntries         = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()));
            Thread.Sleep(Configuration.RefreshTimeMS);
        }

        public void Init()
        {
            Logger.Init();
            Logger.Info("Waiting for game process ...");
            
            while (Process.GetProcessesByName("Baldur").Length == 0)
            {
                if (Process.GetProcessesByName("icewind").Length > 0)
                {
                    gameName = "icewind";
                    break;
                }
                    
                Thread.Sleep(3000);
            }

            this.Proc = Process.GetProcessesByName(gameName)[0];
            Logger.Info("Game process found!");

            ProcessFound?.Invoke(Proc.ProcessName, Proc.Id);

            Configuration.Init(Process.GetProcessesByName(gameName)[0]);
            this.TextEntries     = new ObservableCollection<string>();
            this.ResourceManager = new ResourceManager();
            ResourceManager.Init();
            makeBorderless(Proc.MainWindowHandle);
            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, Proc.Id);
            this.moduleBase = WinAPIBindings.GetModuleBaseAddress(Proc, $"{gameName}.exe");
            this.entityList = new List<BGEntity>();
            Configuration.hProc = hProc;

            ProcessHooked?.Invoke(Proc.ProcessName, Proc.Id);
        }

        bool clip(BGEntity entity1)
        {
            var checkX = entity1.X > entity1.MousePosX1 && entity1.X < entity1.MousePosX1 + entity1.ViewportWidth;
            var checkY = entity1.Y > entity1.MousePosY1 && entity1.Y < entity1.MousePosY1 + entity1.ViewportHeight;
            return checkY & checkX;
        }
        
        public void makeBorderless(IntPtr handle)
        {
            Configuration.HWndPtr = handle;
            if (!Configuration.Borderless) 
                return;
            Configuration.ForceBorderless();            
        }

        public Process GetHookedProcess()
        {
            return Proc;
        }
    }
}
