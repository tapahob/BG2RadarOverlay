using System;
using System.Collections.Concurrent;
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
        public Process proc;
        private IntPtr hProc;        
        private IntPtr moduleBase;
        public ConcurrentBag<BGEntity> entityList;

        public ObservableCollection<String> TextEntries { get; set; }
        public ResourceManager ResourceManager { get; private set; }
        public IEnumerable<BGEntity> NearestEnemies { get; private set; }

        public void MainLoop()
        {
            System.Diagnostics.Stopwatch sw = new Stopwatch();

            var entityListTemp = new ConcurrentBag<BGEntity>();
            var All = new List<BGEntity>();
            BGEntity main = null;

            var staticEntityList = moduleBase + 0x005408EC;
            var test = WinAPIBindings.FindDMAAddy(staticEntityList, new int[] { }) - 4;
            var length = WinAPIBindings.ReadInt32(test - 8);

            var start = moduleBase + 0x00540FE4 - 5000;
            sw.Start();
            for (int i = 0; i < length*8; i+=8)
            {
                var index = WinAPIBindings.ReadInt32(test + i);
                //if (index == 0x0000FFFF)
                //{
                //    var t = i / 8;
                //    break;
                //}

                var entityAddr = WinAPIBindings.FindDMAAddy(test + i + 0x4).ToString();
                if (entityAddr.Length < 6)
                {
                    length++;
                    i -= 4;
                    continue;
                }

                var entityPtr = WinAPIBindings.FindDMAAddy(test + i + 0x4);

                //if (ReadInt32(start + 0x8 * i) == 65535) 
                //    continue;
                var newEntity = new BGEntity(ResourceManager, entityPtr);
                if (!newEntity.Loaded) 
                    continue;
                
                All.Add(newEntity);
                if (main == null && newEntity?.Reader?.EnemyAlly == 2)
                {
                    main = newEntity;
                }

                if (newEntity.CurrentHP == 0
                    //|| newEntity.ToString() == "NO .CRE INFO"
                    || newEntity.Reader == null
                    //|| 
                    || newEntity.Reader.EnemyAlly == 128
                    || (newEntity.Reader.EnemyAlly != 255
                    && newEntity.Reader.EnemyAlly != 5                    
                    && newEntity.Reader.EnemyAlly != 28)
                    || newEntity.Reader.Class1Level == 0
                    || newEntity.Reader.Class == CREReader.CLASS.INNOCENT
                    || newEntity.CreResourceFilename == "TIMOEN.CRE"
                    || newEntity.Reader.Class == CREReader.CLASS.NO_CLASS)
                    continue;
                if (newEntity?.AreaName == main?.AreaName && newEntity.Type == 49)
                {
                    newEntity.tag = index.ToString();
                    entityListTemp.Add(newEntity);                    
                }
            }
            entityList = new ConcurrentBag<BGEntity>(entityListTemp);
            var ms2 = sw.Elapsed.TotalMilliseconds;
            var nearestThings = All.Where(y => len(main, y) < Configuration.RadarRadius);
            this.NearestEnemies = entityListTemp.Where(y => len(main, y) < Configuration.RadarRadius);
            TextEntries = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()).ToList());
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            Thread.Sleep(500);
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

        double len(BGEntity entity1, BGEntity entity2)
        {
            if (entity1 == null || entity2 == null) return 99999;
            var x = Math.Pow(entity1.X - entity2.X, 2);
            var y = Math.Pow(entity1.Y - entity2.Y, 2);
            return Math.Sqrt(x + y);
        }
        

        private void makeBorderless(IntPtr handle)
        {
            if (!Configuration.Borderless) 
                return;
            
            uint currentStyle = (uint) WinAPIBindings.GetWindowLongPtr(handle, -16).ToInt64();
            WinAPIBindings.SetWindowLong32(handle, -16, currentStyle & ~0x00800000 | 0x00400000);
            WinAPIBindings.ShowWindow(handle.ToInt32(), 3);
        }
    }
}
