using Gma.System.MouseKeyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BGOverlay
{
    public static class MouseHook
    {
        public static event empty MouseEvent;
        public delegate void empty();
        public static void InstallHook()
        {
            Hook.GlobalEvents().MouseClick += (o, e) => { if (e.Button == MouseButtons.Right) MouseEvent(); };
        }
    }
}
