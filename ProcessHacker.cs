using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using WinApiBindings;
using static WinApiBindings.WinAPIBindings;

namespace BGOverlay
{
    public class ProcessHacker
    {
        private Process proc;
        private IntPtr hProc;
        private IntPtr modBase;
        private IntPtr modBase2;
        private List<BGEntity> entityList;

        public ObservableCollection<String> TextEntries { get; set; }
        public ResourceManager ResourceManager { get; private set; }
        public IEnumerable<BGEntity> NearestEnemies { get; private set; }

        public void MainLoop()
        {
            entityList.Clear();
            var All = new List<BGEntity>();
            BGEntity main = null;
            for (int i = 0; i < 575; ++i)
            {
                var newEntity = new BGEntity(ResourceManager, hProc, modBase2 + 0x541020 - 0x738 + 0x8 * i);
                All.Add(newEntity);
                if (main == null && newEntity?.Reader?.EnemyAlly == 2)
                {
                    main = newEntity;
                }
                
                if (newEntity.CurrentHP == 0
                    || newEntity.ToString() == "NO .CRE INFO" 
                    || newEntity.Reader.EnemyAlly != 255 
                    && newEntity.Reader.EnemyAlly != 5 
                    && newEntity.Reader.EnemyAlly != 128
                    || newEntity.Reader.Class1Level == 0
                    || newEntity.Reader.Class == CREReader.CLASS.INNOCENT
                    || newEntity.CreResourceFilename == "TIMOEN.CRE"
                    || newEntity.Reader.Class == CREReader.CLASS.NO_CLASS) continue;
                if (newEntity.Type == 49)
                {
                    entityList.Add(newEntity);
                }
            }
            this.NearestEnemies = entityList.Where(y => len(main, y) < 300);
            TextEntries = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()).ToList());
            Thread.Sleep(500);
        }

        public void Init()
        {
            Configuration.Init();
            this.TextEntries = new ObservableCollection<string>();
            this.ResourceManager = new ResourceManager();
            ResourceManager.Init();

            this.proc       = Process.GetProcessesByName("Baldur")[0];
            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, proc.Id);
            this.modBase    = WinAPIBindings.GetModuleBaseAddress(proc, "Baldur.exe");
            this.modBase2   = WinAPIBindings.GetModuleBaseAddress(proc.Id, "Baldur.exe");
            this.entityList = new List<BGEntity>();
        }

        double len(BGEntity entity1, BGEntity entity2)
        {
            if (entity1 == null || entity2 == null) return 99999;
            var x = Math.Pow(entity1.X - entity2.X, 2);
            var y = Math.Pow(entity1.Y - entity2.Y, 2);
            return Math.Sqrt(x + y);
        }
    }
}
