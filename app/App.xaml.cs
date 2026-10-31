using System;
using System.Linq;
using System.Windows;

namespace ReaSense
{
    public partial class App : Application
    {
        public const string AppName = "REA Sense";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Contains("--selftest"))
            {
                try { FanCurve.SelfTest(); SmartTune.SelfTest(); Shutdown(0); }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
                return;
            }

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
