using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace ReaSense
{
    public partial class MainWindow : Window
    {
        const int PanicTemp = 92;            // Custom and Curve force 100% at or above this
        const string TaskName = "ReaSense";

        readonly Settings S;
        readonly Support Sup;
        readonly Lighting Light = new Lighting();
        readonly DispatcherTimer FanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly DispatcherTimer Anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly DateTime AnimStart = DateTime.Now;
        readonly WinForms.NotifyIcon Tray;
        readonly Rectangle[] BigZones = new Rectangle[4];
        readonly Button[] ZoneSwatches = new Button[4];
        readonly System.Collections.Generic.List<(Effect Effect, Rectangle[] Zones, RadioButton Tile)> Tiles =
            new System.Collections.Generic.List<(Effect, Rectangle[], RadioButton)>();

        bool Allowed;                         // fan, GPU and lighting writes permitted on this model
        string Mode = "Auto";
        FanCurve Curve, Cpu, Gpu;             // Curve = parsed text; Cpu/Gpu carry each fan's smoothing state
        double LastCpuTemp = 50;
        bool Loading = true, Quitting;

        public MainWindow(Settings settings, bool startHidden)
        {
            S = settings;
            InitializeComponent();
            Sup = Support.Detect();
            ModelText.Text = Sup.Model;
            Light.CpuTemp = () => LastCpuTemp;
            Light.Failed += msg => LightMsg.Text = "Lighting stopped: " + msg;

            BuildLightingControls();
            CpuSlider.Value = S.CpuPercent;
            GpuSlider.Value = S.GpuPercent;
            CurveText.Text = S.Curve;
            BrightSlider.Value = S.Brightness;
            SpeedSlider.Value = S.Speed;
            (S.Direction == 2 ? DirLtr : DirRtl).IsChecked = true;

            Tray = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName),
                Text = App.AppName,
                Visible = true,
                ContextMenu = new WinForms.ContextMenu(new[]
                {
                    new WinForms.MenuItem("Open", (s, e) => ShowFromTray()),
                    new WinForms.MenuItem("Quit", (s, e) => Quit()),
                }),
            };
            Tray.DoubleClick += (s, e) => ShowFromTray();

            ApplyAllowed();
            ModeAuto.IsChecked = true;
            if (Allowed && S.FanMode != "Auto") RadioFor(S.FanMode).IsChecked = true;
            if (Sup.GpuOc && Acer.GetGpuOc() is int oc) new[] { OcNormal, OcFaster, OcTurbo }.ElementAtOrDefault(oc)?.SetValue(RadioButton.IsCheckedProperty, true);
            RefreshSettingsPage();
            Loading = false;

            // Sleep resets the fans and lighting to the firmware's defaults, so re-apply on resume.
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(() => { SetMode(Mode); ReapplyLighting(); }));
            };

            FanTimer.Tick += (s, e) => { try { UpdateFans(); } catch (Exception ex) { StopManual("Error: " + ex.Message); } };
            FanTimer.Start();
            Anim.Tick += (s, e) => AnimatePreviews();
            Anim.Start();
            try { UpdateFans(); } catch { }
        }

        // ================= Fans =================

        RadioButton RadioFor(string mode) =>
            mode == "Max" ? ModeMax : mode == "Custom" ? ModeCustom : mode == "Curve" ? ModeCurve : ModeAuto;

        void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton r && r.IsChecked == true && Tray != null) SetMode((string)r.Content);
        }

        void SetMode(string m)
        {
            try
            {
                if (Allowed)
                {
                    if (m == "Curve") { Curve = new FanCurve(CurveText.Text); Cpu = new FanCurve(CurveText.Text); Gpu = new FanCurve(CurveText.Text); }
                    Acer.SetFanMode(m == "Max" ? FanMode.Max : m == "Auto" ? FanMode.Auto : FanMode.Custom);
                }
                Mode = m;
                FanMsg.Text = m == "Auto" ? "The laptop controls the fans." : m == "Max" ? "Both fans at full speed." : "";
                if (!Loading && Allowed) { S.FanMode = m; SaveFanSettings(); }
                UpdateFans();
            }
            catch (Exception ex) { StopManual("Error: " + ex.Message); }
        }

        // Any failure while we hold the fans hands them back to the laptop.
        void StopManual(string why)
        {
            if (Mode != "Auto") { try { if (Allowed) Acer.SetFanMode(FanMode.Auto); } catch { } }
            Mode = "Auto";
            ModeAuto.IsChecked = true;
            FanMsg.Text = why + " Switched to Auto.";
        }

        static int? Temp(int sensor) { var t = Acer.Sensor(sensor); return t > 0 ? t : null; }   // 0 means no reading

        void UpdateFans()
        {
            if (!Sup.Sensors) return;
            int? ct = Temp(Acer.CpuTemp), gt = Temp(Acer.GpuTemp);
            int? cr = Acer.Sensor(Acer.CpuFanRpm), gr = Acer.Sensor(Acer.GpuFanRpm);
            if (ct != null) LastCpuTemp = ct.Value;
            CpuTemp.Text = ct != null ? $"{ct}°C" : "--°C";
            GpuTemp.Text = gt != null ? $"{gt}°C" : "--°C";
            CpuFan.Text = $"{cr} rpm";
            GpuFan.Text = $"{gr} rpm";
            Tray.Text = $"CPU {ct}°C  GPU {gt}°C  ({Mode})";
            SideStatus.Text = $"Fans: {Mode}";
            DrawCurveMarker(ct);

            if (Mode != "Custom" && Mode != "Curve") return;
            if (ct == null || gt == null) { StopManual("Lost the temperature reading."); return; }
            int c, g;
            if (Mode == "Curve") { c = Cpu.Next(ct.Value); g = Gpu.Next(gt.Value); }
            else { c = (int)CpuSlider.Value; g = (int)GpuSlider.Value; }
            if (ct >= PanicTemp) c = 100;
            if (gt >= PanicTemp) g = 100;
            // Re-sent every tick: the firmware can drop custom speeds (after sleep, for example).
            Acer.SetFanSpeed(true, c);
            Acer.SetFanSpeed(false, g);
            FanMsg.Text = $"CPU fan {c}%   GPU fan {g}%";
        }

        void ApplyCurve_Click(object sender, RoutedEventArgs e)
        {
            try { FanCurve.Parse(CurveText.Text); }
            catch (FormatException ex) { FanMsg.Text = ex.Message; return; }
            S.Curve = CurveText.Text;
            ModeCurve.IsChecked = true;
            SetMode("Curve");
        }

        void SaveFanSettings()
        {
            S.CpuPercent = (int)CpuSlider.Value; S.GpuPercent = (int)GpuSlider.Value;
            S.Save();
        }

        void CurveText_TextChanged(object sender, TextChangedEventArgs e) => DrawCurve();

        // Plot spans 30-100°C by 0-100%.
        Point Plot(double temp, double pct) =>
            new Point((temp - 30) / 70 * CurvePlot.ActualWidth, (1 - pct / 100) * CurvePlot.ActualHeight);

        void DrawCurve()
        {
            CurveLine.Points.Clear();
            try
            {
                var pts = FanCurve.Parse(CurveText.Text);
                for (int t = 30; t <= 100; t += 2) CurveLine.Points.Add(Plot(t, FanCurve.Interpolate(pts, t)));
            }
            catch (FormatException) { }
        }

        void DrawCurveMarker(int? temp)
        {
            if (CurveLine.Points.Count == 0) DrawCurve();
            if (temp == null) { CurveNow.Visibility = Visibility.Collapsed; return; }
            var x = Plot(temp.Value, 0).X;
            CurveNow.X1 = CurveNow.X2 = x; CurveNow.Y1 = 0; CurveNow.Y2 = CurvePlot.ActualHeight;
            CurveNow.Visibility = Visibility.Visible;
        }

        // ================= Lighting =================

        Color[] ZoneColors() => ZoneSwatches.Select(b => ((SolidColorBrush)b.Background).Color).ToArray();
        Effect SelectedEffect => Tiles.FirstOrDefault(t => t.Tile.IsChecked == true).Effect ?? Effects.Find(S.Effect);

        void BuildLightingControls()
        {
            for (int z = 0; z < 4; z++)
            {
                BigZones[z] = new Rectangle { RadiusX = 10, RadiusY = 10, Margin = new Thickness(4) };
                BigPreview.Children.Add(BigZones[z]);

                var color = (Color)ColorConverter.ConvertFromString(S.ZoneColors.ElementAtOrDefault(z) ?? "#00AEC7");
                var sw = new Button { Width = 64, Height = 40, Margin = new Thickness(0, 0, 10, 0), Background = new SolidColorBrush(color), ToolTip = $"Zone {z + 1}" };
                sw.Click += (s, e) =>
                {
                    var b = (Button)s; var c = ((SolidColorBrush)b.Background).Color;
                    using (var dlg = new WinForms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) })
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                            b.Background = new SolidColorBrush(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
                };
                ZoneSwatches[z] = sw;
                ZoneButtons.Children.Add(sw);
            }

            foreach (var fx in Effects.All)
            {
                var mini = new UniformGrid { Columns = 4, Height = 26, Margin = new Thickness(0, 0, 0, 8) };
                var rects = Enumerable.Range(0, 4).Select(_ => new Rectangle { RadiusX = 4, RadiusY = 4, Margin = new Thickness(1.5) }).ToArray();
                foreach (var r in rects) mini.Children.Add(r);
                var content = new StackPanel();
                content.Children.Add(mini);
                content.Children.Add(new TextBlock { Text = fx.Name, FontSize = 13 });
                var tile = new RadioButton { Style = (Style)FindResource("EffectTile"), GroupName = "Effect", Content = content, ToolTip = fx.Description };
                tile.Checked += Preview_Changed;
                tile.IsChecked = fx.Name == S.Effect;
                (fx.Native != null ? NativeEffects : AppEffects).Children.Add(tile);
                Tiles.Add((fx, rects, tile));
            }
        }

        void Preview_Changed(object sender, RoutedEventArgs e)
        {
            if (EffectDesc == null || Tiles.Count < Effects.All.Length) return;
            var fx = SelectedEffect;
            EffectDesc.Text = fx.Description;
            SpeedRow.Visibility = fx.Name == "Static" || fx.Name == "Temperature" ? Visibility.Collapsed : Visibility.Visible;
            DirRow.Visibility = fx.UsesDirection ? Visibility.Visible : Visibility.Collapsed;
        }

        void AnimatePreviews()
        {
            if (!IsVisible || PageLighting.Visibility != Visibility.Visible) return;
            double t = (DateTime.Now - AnimStart).TotalSeconds;
            var zones = ZoneColors();
            int speed = (int)SpeedSlider.Value, dir = DirLtr.IsChecked == true ? 2 : 1;
            double k = BrightSlider.Value / 100;
            foreach (var (fx, rects, _) in Tiles)
            {
                var f = Effects.Frame(fx, t, zones, speed, dir, LastCpuTemp);
                for (int z = 0; z < 4; z++) rects[z].Fill = new SolidColorBrush(f[z]);
            }
            var big = Effects.Frame(SelectedEffect, t, zones, speed, dir, LastCpuTemp);
            for (int z = 0; z < 4; z++)
                BigZones[z].Fill = new SolidColorBrush(Color.FromRgb((byte)(big[z].R * k), (byte)(big[z].G * k), (byte)(big[z].B * k)));
        }

        void ApplyLighting_Click(object sender, RoutedEventArgs e)
        {
            var fx = SelectedEffect;
            S.Effect = fx.Name;
            S.Brightness = (int)BrightSlider.Value; S.Speed = (int)SpeedSlider.Value; S.Direction = DirLtr.IsChecked == true ? 2 : 1;
            S.ZoneColors = ZoneColors().Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}").ToArray();
            S.Save();
            ReapplyLighting();
        }

        void ReapplyLighting()
        {
            if (!Allowed || !Sup.Keyboard) return;
            try
            {
                var colors = S.ZoneColors.Select(h => (Color)ColorConverter.ConvertFromString(h)).ToArray();
                Light.Apply(Effects.Find(S.Effect), colors, S.Brightness, S.Speed, S.Direction);
                LightMsg.Text = $"{S.Effect} applied.";
            }
            catch (Exception ex) { LightMsg.Text = "Failed: " + ex.Message; }
        }

        // ================= Performance =================

        void Oc_Click(object sender, RoutedEventArgs e)
        {
            var r = (RadioButton)sender;
            try { Acer.SetGpuOc(int.Parse((string)r.Tag)); OcMsg.Text = $"GPU overclock: {r.Content}."; }
            catch (Exception ex) { OcMsg.Text = "Failed: " + ex.Message; }
        }

        // ================= Settings =================

        // Greys out controls this laptop can't (or isn't trusted to) use.
        void ApplyAllowed()
        {
            bool ok = Sup.Fans && (Sup.Tested || S.AllowUntested);
            if (!ok && Mode != "Auto") StopManual("Controls aren't allowed on this model.");
            Allowed = ok;
            foreach (var r in new[] { ModeMax, ModeCustom, ModeCurve }) r.IsEnabled = ok;
            foreach (var r in new[] { OcNormal, OcFaster, OcTurbo }) r.IsEnabled = ok && Sup.GpuOc;
            if (!Sup.Fans) FanMsg.Text = "Fan control isn't available on this laptop. See Settings.";
            else if (!ok) FanMsg.Text = "Untested model: controls are off. You can allow them in Settings.";
        }

        static string YesNo(bool b) => b ? "yes" : "no";

        void RefreshSettingsPage()
        {
            ChkStartup.IsChecked = StartupTaskExists();
            ChkDisclaimer.IsChecked = !S.HideDisclaimer;
            ChkUntested.IsChecked = S.AllowUntested;
            ChkUntested.Visibility = Sup.Tested ? Visibility.Collapsed : Visibility.Visible;
            CompatText.Text =
                $"Model: {Sup.Model} (tested: {YesNo(Sup.Tested)})\n" +
                $"Acer gaming WMI: {YesNo(Sup.Wmi)}\n" +
                $"Temperature sensors: {YesNo(Sup.Sensors)}\n" +
                $"Fan control: {YesNo(Sup.Fans)}\n" +
                $"GPU overclock presets: {YesNo(Sup.GpuOc)}\n" +
                $"Keyboard lighting: {YesNo(Sup.Keyboard)}";
            var v = typeof(App).Assembly.GetName().Version;
            AboutText.Text = $"{App.AppName} {v.Major}.{v.Minor}.{v.Build}. Free and open source under the MIT license.\n" +
                             "Not made, endorsed or supported by Acer. Acer and Predator are trademarks of Acer Inc.";
        }

        void ChkDisclaimer_Click(object sender, RoutedEventArgs e) { S.HideDisclaimer = ChkDisclaimer.IsChecked != true; S.Save(); }

        void ChkUntested_Click(object sender, RoutedEventArgs e)
        {
            S.AllowUntested = ChkUntested.IsChecked == true; S.Save();
            ApplyAllowed();
        }

        static bool StartupTaskExists() => RunSchtasks($"/Query /TN {TaskName}") == 0;

        // A logon task with highest privileges starts the app elevated without a UAC prompt.
        void ChkStartup_Click(object sender, RoutedEventArgs e)
        {
            var exe = Process.GetCurrentProcess().MainModule.FileName;
            int rc = ChkStartup.IsChecked == true
                ? RunSchtasks($"/Create /F /RL HIGHEST /SC ONLOGON /TN {TaskName} /TR \"\\\"{exe}\\\" --minimized\"")
                : RunSchtasks($"/Delete /F /TN {TaskName}");
            SettingsMsg.Text = rc == 0
                ? (ChkStartup.IsChecked == true ? "Starts with Windows from this location. If you move the .exe, tick this again." : "Won't start with Windows.")
                : "Couldn't change the startup task.";
            ChkStartup.IsChecked = StartupTaskExists();
        }

        static int RunSchtasks(string args)
        {
            using (var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        // ================= Window =================

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageFans == null) return;
            PageFans.Visibility = NavFans.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLighting.Visibility = NavLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PagePerformance.Visibility = NavPerformance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (NavLighting.IsChecked == true) Preview_Changed(null, null);
        }

        void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
        void Minimize_Click(object sender, RoutedEventArgs e) => Hide();
        void Close_Click(object sender, RoutedEventArgs e) => Quit();

        void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }

        protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); DrawCurve(); }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!Quitting) { e.Cancel = true; Quit(); }
            base.OnClosing(e);
        }

        // Hands everything back to the firmware: fans to auto, GPU to normal.
        void Quit()
        {
            Quitting = true;
            FanTimer.Stop(); Anim.Stop(); Light.Stop();
            if (Allowed)
            {
                try { Acer.SetFanMode(FanMode.Auto); } catch { }
                if (Sup.GpuOc && OcNormal.IsChecked != true) try { Acer.SetGpuOc(0); } catch { }
            }
            Tray.Visible = false;
            Tray.Dispose();
            Application.Current.Shutdown();
        }
    }
}
