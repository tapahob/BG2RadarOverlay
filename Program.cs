using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinApiBindings;

namespace BGOverlay
{
    class Program
    {
        static void Main(string[] args)
        {
            Init();
            Process proc = Process.GetProcessesByName("Baldur")[0];

            var hProc = WinAPIBindings.OpenProcess(WinAPIBindings.ProcessAccessFlags.All, false, proc.Id);
            var modBase = WinAPIBindings.GetModuleBaseAddress(proc, "Baldur.exe");
            var modBase2 = WinAPIBindings.GetModuleBaseAddress(proc.Id, "Baldur.exe");

            var entityList = new List<BGEntity>();

            while (true)
            {
                BGEntity main = null;
                for (int i = 15; i < 275; ++i)
                {
                    var newEntity = new BGEntity(hProc, modBase + 0x541020 - 0x738 + 0x8 * i);
                    if (
                        //newEntity.Id != 65535
                        //&& 
                        newEntity.Type == 49
                        )
                    {
                        entityList.Add(newEntity);
                    }

                    if (newEntity.Id == 228)
                    {
                        main = newEntity;
                    }
                }

                entityList.Where(y => len(main, y) < 300).ToList().ForEach(x => Console.WriteLine(x.ToString()));
                Thread.Sleep(3000);
                Console.Clear();
                entityList.Clear();
            }
        }
        static void Init()
        {
            var keyReader = new KeyReader();
            var creEntries = KeyReader.CREResorceEntries;
            creEntries.ForEach(x => x.LoadCREFiles());

            int i = 0;
        }

        static double len(BGEntity entity1, BGEntity entity2)
        {
            var x = Math.Pow(entity1.X - entity2.X, 2);
            var y = Math.Pow(entity1.Y - entity2.Y, 2);
            return Math.Sqrt(x + y);
        }
    }
}
