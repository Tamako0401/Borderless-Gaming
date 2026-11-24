using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using BorderlessGaming.Forms;
using BorderlessGaming.Logic.Models;
using BorderlessGaming.Logic.System;
using BorderlessGaming.Logic.Windows;

namespace BorderlessGaming
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
          
            var packageDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var testMarker = Path.Combine(packageDirectory, "steins-gate-test.txt");
            if (File.Exists(testMarker))
            {
                Environment.SetEnvironmentVariable("BG_MOUSE_PROBE_AUTO", "1");
                Environment.SetEnvironmentVariable("BG_MOUSE_PROBE_MODE", "input");
                Environment.SetEnvironmentVariable("BG_MOUSE_TEST_DATA", Path.Combine(packageDirectory, "test-data"));
                Environment.SetEnvironmentVariable("BG_MOUSE_PROBE_LOG", Path.Combine(packageDirectory, "mouse-test.log"));

                var executable = File.ReadAllText(testMarker).Trim();
                if (executable.Length != 0)
                    Environment.SetEnvironmentVariable("BG_MOUSE_PROBE_EXE", executable);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Tools.Setup();
            ForegroundManager.Subscribe();
            Application.Run(new MainWindow());
          
        }
    }
}
