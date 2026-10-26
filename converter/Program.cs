using System;
using System.Windows.Forms;

namespace FlashbackConverter
{
    static class Program
    {
        // Clips or folders dropped on the .exe (or given on the command line) start
        // in the list.
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }
}
