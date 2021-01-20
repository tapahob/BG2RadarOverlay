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
            //while (true)
            //{
            entityList.Clear();
            BGEntity main = null;
            for (int i = 15; i < 275; ++i)
            {
                var newEntity = new BGEntity(ResourceManager, hProc, modBase2 + 0x541020 - 0x738 + 0x8 * i);
                if (newEntity.Type == 49)
                {
                    entityList.Add(newEntity);
                }

                if (newEntity.Id == 228)
                {
                    main = newEntity;
                }
            }
            this.NearestEnemies = entityList.Where(y => len(main, y) < 300);
            TextEntries = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()).ToList());
            Thread.Sleep(3000);
        }

        public void Init()
        {
            this.TextEntries = new ObservableCollection<string>();
            this.ResourceManager = new ResourceManager();
            ResourceManager.Init();
            
            var creEntries = ResourceManager.CREResorceEntries;
            creEntries.ForEach(x => x.LoadCREFiles());

            this.proc       = Process.GetProcessesByName("Baldur")[0];


            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, proc.Id);
            this.modBase    = WinAPIBindings.GetModuleBaseAddress(proc, "Baldur.exe");
            this.modBase2   = WinAPIBindings.GetModuleBaseAddress(proc.Id, "Baldur.exe");
            this.entityList = new List<BGEntity>();
        }

        double len(BGEntity entity1, BGEntity entity2)
        {
            var x = Math.Pow(entity1.X - entity2.X, 2);
            var y = Math.Pow(entity1.Y - entity2.Y, 2);
            return Math.Sqrt(x + y);
        }

        static void Main(String[] args)
        {
            var ph = new ProcessHacker();
            ph.Init();
            ph.MainLoop();
        }
    }
}
