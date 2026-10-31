using System;
using System.Linq;
using System.Windows;

namespace TalonForge
{
    public partial class App : Application
    {
        public const string AppName = "TalonForge";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Contains("--selftest"))
            {
                try { FanCurve.SelfTest(); SmartTune.SelfTest(); Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
                return;
            }

            // Renamed from "ReaSense": carry the old settings and logs over once.
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string oldDir = System.IO.Path.Combine(appData, "ReaSense"), newDir = System.IO.Path.Combine(appData, "TalonForge");
                if (System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(newDir)) System.IO.Directory.Move(oldDir, newDir);
            }
            catch { }

            var settings = Settings.Load();
            if (!settings.HideDisclaimer)
            {
                var d = new DisclaimerWindow();
                if (d.ShowDialog() != true) { Shutdown(0); return; }
                if (d.DontShowAgain) { settings.HideDisclaimer = true; settings.Save(); }
            }

            var w = new MainWindow(settings, startHidden: e.Args.Contains("--minimized"));
            if (!e.Args.Contains("--minimized")) w.Show();
        }
    }
}
