using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using WinApiBindings;

namespace BGOverlay
{
    public class ProcessHacker
    {
        public Process proc;
        private IntPtr hProc;        
        private IntPtr moduleBase;
        public ConcurrentBag<BGEntity> entityList;

        public ObservableCollection<String> TextEntries { get; set; }
        public ResourceManager ResourceManager { get; private set; }
        public IEnumerable<BGEntity> NearestEnemies { get; private set; }

        private BGEntity main;

        public void MainLoop()
        {
            var entityListTemp = new ConcurrentBag<BGEntity>();
            var All = new List<BGEntity>();

            var staticEntityList = moduleBase + 0x68D438 + 0x18;
            var test = WinAPIBindings.FindDMAAddy(staticEntityList, new int[] { });
            var length = WinAPIBindings.ReadInt32(moduleBase + 0x68D434);
            var marginOfError = 500;

            // First i = 32016
            for (int i = 2000 * 16; i < length*16 + marginOfError; i+=16)
            {
                var index = WinAPIBindings.ReadInt32(test + i);
                if (index == 65535)
                    continue;

                var entityPtr = WinAPIBindings.FindDMAAddy(test + i + 0x8);

                var newEntity = new BGEntity(ResourceManager, entityPtr);
                if (!newEntity.Loaded)
                    continue;

                All.Add(newEntity);

                //if (newEntity.CurrentHP == 0
                //    || newEntity.Reader == null
                //    || newEntity.Reader.Class == CREReader.CLASS.INNOCENT
                //    || newEntity.Reader.Class == CREReader.CLASS.NO_CLASS
                //    || newEntity.AreaName == "<ERROR>"
                //    )
                //    continue;

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

                //if (newEntity.Reader.EnemyAlly != 255
                //    || newEntity.Reader.EnemyAlly != 128
                //    || newEntity.Reader.EnemyAlly != 5
                //    || newEntity.Reader.EnemyAlly != 28)
                //    continue;
                newEntity.tag = index;
                entityListTemp.Add(newEntity);                    
            }
            entityList = new ConcurrentBag<BGEntity>(entityListTemp);
            var nearestThings = All.Where(y => clip(y));
            this.NearestEnemies = entityListTemp.Where(y => clip(y));
            TextEntries = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()).ToList());            
            Thread.Sleep(Configuration.RefreshTimeMS);
        }

        public void Init()
        {
            Configuration.Init();
            this.TextEntries = new ObservableCollection<string>();
            this.ResourceManager = new ResourceManager();
            ResourceManager.Init();
            while (Process.GetProcessesByName("Baldur").Length == 0)
            {
                Thread.Sleep(3000);
            }
            this.proc       = Process.GetProcessesByName("Baldur")[0];
            makeBorderless(proc.MainWindowHandle);

            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, proc.Id);
            this.moduleBase = WinAPIBindings.GetModuleBaseAddress(proc, "Baldur.exe");
            this.entityList = new ConcurrentBag<BGEntity>();
            Configuration.hProc = hProc;
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
    }
}
