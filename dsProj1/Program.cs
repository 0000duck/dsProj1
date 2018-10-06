using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace dsProj1
{
    static class Program
    {
        /// The main entry point for the application.

        //For ID generation
        public static Random Rand;

        [STAThread]
        static void Main()
        {
            Rand = new Random();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Main());
        }
    }
}
