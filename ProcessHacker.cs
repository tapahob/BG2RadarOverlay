﻿using System;
using System.Collections.Concurrent;
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
        public Process proc;
        private IntPtr hProc;
        private IntPtr modBase;
        private IntPtr modBase2;
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
            sw.Start();
            for (int i = 18; i < 5000; ++i)
            {
                if (ReadInt32(modBase2 + 0x541020 - 0x738 + 0x8 * i) == 65535) continue;
                var newEntity = new BGEntity(ResourceManager, modBase2 + 0x541020 - 0x738 + 0x8 * i);
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
                    || newEntity.Reader.EnemyAlly != 255
                    && newEntity.Reader.EnemyAlly != 5
                    && newEntity.Reader.EnemyAlly != 128
                    && newEntity.Reader.EnemyAlly != 28
                    || newEntity.Reader.Class1Level == 0
                    || newEntity.Reader.Class == CREReader.CLASS.INNOCENT
                    || newEntity.CreResourceFilename == "TIMOEN.CRE"
                    || newEntity.Reader.Class == CREReader.CLASS.NO_CLASS) continue;
                if (newEntity.Type == 49)
                {
                    entityListTemp.Add(newEntity);
                }
            }
            entityList = new ConcurrentBag<BGEntity>(entityListTemp);
            var ms2 = sw.Elapsed.TotalMilliseconds;
            var nearestThings = All.Where(y => len(main, y) < 300);
            this.NearestEnemies = entityListTemp.Where(y => len(main, y) < 300);
            TextEntries = new ObservableCollection<string>(NearestEnemies.Select(x => x.ToString()).ToList());
            sw.Stop();
            var ms = sw.Elapsed.TotalMilliseconds;
            //Thread.Sleep(500);
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
            this.hProc      = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, proc.Id);
            this.modBase    = WinAPIBindings.GetModuleBaseAddress(proc, "Baldur.exe");
            this.modBase2   = WinAPIBindings.GetModuleBaseAddress(proc.Id, "Baldur.exe");
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
    }
}
