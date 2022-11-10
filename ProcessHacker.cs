using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WinApiBindings;

namespace BGOverlay
{
    public class ProcessHacker
    {
        public Process Proc;
        private IntPtr hProc;        
        private IntPtr moduleBase;
        public event EventHandler ProcessCreated;
        public List<BGEntity> entityList;

        public ObservableCollection<String> TextEntries = new ObservableCollection<string>();
        public ResourceManager ResourceManager { get; private set; }
        public List<BGEntity> NearestEnemies { get; private set; }

        private BGEntity main;

        private List<BGEntity> entityListTemp = new List<BGEntity>();
        private List<BGEntity> allEntities    = new List<BGEntity>();

        public void MainLoop()
        {
            entityListTemp.Clear();
            allEntities.Clear();
            if (Proc.HasExited)
            {
                NearestEnemies.Clear();
                TextEntries.Clear();
                ProcessCreated.Invoke(null, null);
                this.Init();
            }

            var staticEntityList = moduleBase + 0x68D438 + 0x18;
            var test             = WinAPIBindings.FindDMAAddy(staticEntityList, new int[] { });
            var length           = WinAPIBindings.ReadInt32(moduleBase + 0x68D434);
            var marginOfError    = 500;
                        
            // First i = 32016
            for (int i = 2000 * 16; i < length * 16 + marginOfError; i += 16)
            {
                try
                {
                    var index = WinAPIBindings.ReadInt32(test + i);
                    if (index == 65535)
                        continue;

                    var entityPtr = WinAPIBindings.FindDMAAddy(test + i + 0x8);

                    var newEntity = new BGEntity(ResourceManager, entityPtr);
                    if (!newEntity.Loaded)
                        continue;

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
                        
            entityList          = entityListTemp;
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
                Thread.Sleep(3000);
            }
            Logger.Info("Game process found!");
            Configuration.Init(Process.GetProcessesByName("Baldur")[0]);
            this.TextEntries     = new ObservableCollection<string>();
            this.ResourceManager = new ResourceManager();
            ResourceManager.Init();
            this.Proc       = Process.GetProcessesByName("Baldur")[0];
            makeBorderless(Proc.MainWindowHandle);
            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, Proc.Id);
            this.moduleBase = WinAPIBindings.GetModuleBaseAddress(Proc, "Baldur.exe");
            this.entityList = new List<BGEntity>();
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
