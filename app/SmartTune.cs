using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ReaSense
{
    // CPU and GPU usage, read once per fan tick with Refresh(). Null when unavailable.
    // CPU from Windows performance counters; GPU from nvidia-smi (ships with the NVIDIA driver), because
    // Windows' GPU Engine counters miss CUDA work such as ComfyUI.
    public static class Usage
    {
        static PerformanceCounter _cpu, _perf;
        static readonly string Smi = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");

        public static int? CpuLoad, CpuSpeed, GpuLoad, GpuMhz;   // CpuSpeed is % of base clock; above 100 = turbo
        public static double? GpuWatts;

        static PerformanceCounter Make(string cat, string name, string inst)
        {
            try { var c = new PerformanceCounter(cat, name, inst, true); c.NextValue(); return c; } catch { return null; }
        }

        static int? Read(PerformanceCounter c, double max = double.MaxValue)
        {
            try { return c == null ? (int?)null : (int)Math.Round(Math.Min(max, c.NextValue())); } catch { return null; }
        }

        public static void Refresh()
        {
            _cpu = _cpu ?? Make("Processor Information", "% Processor Utility", "_Total") ?? Make("Processor", "% Processor Time", "_Total");
            _perf = _perf ?? Make("Processor Information", "% Processor Performance", "_Total");
            CpuLoad = Read(_cpu, 100);
            CpuSpeed = Read(_perf);
            GpuLoad = GpuMhz = null; GpuWatts = null;
            if (!File.Exists(Smi)) return;
            try
            {
                // ponytail: one short process per 2 s tick (~50-100 ms); switch to NVML if that ever shows up as lag.
                var psi = new ProcessStartInfo(Smi, "--query-gpu=utilization.gpu,clocks.gr,power.draw --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var parts = p.StandardOutput.ReadLine()?.Split(',');
                    p.WaitForExit(1000);
                    if (parts == null || parts.Length < 3) return;
                    double v;
                    if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) GpuLoad = (int)v;
                    if (double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) GpuMhz = (int)v;
                    if (double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) GpuWatts = Math.Round(v, 1);
                }
            }
            catch { }
        }
    }

    // Appends one line per fan tick to %APPDATA%\ReaSense\fanlog.csv (opens in Excel),
    // and to a separate monitor-*.csv in the logs folder while the user has logging switched on.
    public static class FanLog
    {
        public static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReaSense");
        public static readonly string Path = System.IO.Path.Combine(Dir, "fanlog.csv");
        public static readonly string LogsDir = System.IO.Path.Combine(Dir, "logs");
        const long MaxBytes = 5_000_000;   // about 2 days at 2 s ticks; the previous file is kept as fanlog.old.csv
        const string Header = "time,mode,cpu_temp,gpu_temp,cpu_fan_pct,gpu_fan_pct,cpu_rpm,gpu_rpm,cpu_load_pct,cpu_speed_pct_of_base,gpu_load_pct,gpu_mhz,gpu_watts";

        public static string Session { get; private set; }
        public static int SessionRows { get; private set; }

        public static void StartSession()
        {
            Directory.CreateDirectory(LogsDir);
            Session = System.IO.Path.Combine(LogsDir, $"monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(Session, Header + "\r\n");
            SessionRows = 0;
        }

        public static void StopSession() => Session = null;

        public static void Write(string mode, int? cpuTemp, int? gpuTemp, int? cpuPct, int? gpuPct, int? cpuRpm, int? gpuRpm)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var f = new FileInfo(Path);
                if (f.Exists && (f.Length > MaxBytes || File.ReadLines(Path).FirstOrDefault() != Header))
                {
                    var old = System.IO.Path.ChangeExtension(Path, ".old.csv");
                    File.Delete(old); f.MoveTo(old);
                }
                if (!File.Exists(Path)) File.WriteAllText(Path, Header + "\r\n");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{mode},{cpuTemp},{gpuTemp},{cpuPct},{gpuPct},{cpuRpm},{gpuRpm},{Usage.CpuLoad},{Usage.CpuSpeed},{Usage.GpuLoad},{Usage.GpuMhz},{Usage.GpuWatts?.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n";
                File.AppendAllText(Path, line);
                if (Session != null) { File.AppendAllText(Session, line); SessionRows++; }
            }
            catch { }   // logging must never stop fan control
        }
    }

    // Built-in CPU load for Smart Tune: one busy thread per core, below-normal priority so the app stays responsive.
    // ponytail: CPU only; GPU load still comes from the user's own workload (game, ComfyUI) until a shader load is added.
    public static class CpuLoad
    {
        static volatile bool _run;
        public static bool Running => _run;

        public static void Start()
        {
            if (_run) return;
            _run = true;
            for (int i = 0; i < Environment.ProcessorCount; i++)
                new System.Threading.Thread(() => { double x = 1; while (_run) x = Math.Sqrt(x * x + 1.0001); })
                { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        public static void Stop() => _run = false;
    }

    // Smart Tune: holds both fans at each test level under the user's own load until temperatures settle,
    // then picks the level past which more fan buys little cooling, and builds a gentle curve around it.
    public class SmartTune
    {
        // Starts at full speed and steps down, so it ends at the level the laptop can't hold below LimitTemp.
        public static readonly int[] Levels = { 100, 85, 70, 55, 40 };
        public const int LimitTemp = 98;                    // i7-10750H Tjmax is 100°C (it throttles there); Kyle chose 98
        public const int MinHoldS = 120, MaxHoldS = 240;   // per level: 10-20 minutes in all
        // A step up is worth its noise if it gains at least this much per 1000 rpm added (both fans). The gain is the
        // biggest of: CPU cooling, GPU cooling, or CPU speed (5% of base clock counts as 1°C). The i7-10750H holds itself
        // near 92°C under load and trades fan for clock speed instead of temperature, so speed has to count.
        public const double WorthDegPer1000Rpm = 2.0, SpeedPctPerDeg = 5.0;
        public const double MaxSlope = 3.0;                 // most % per °C anywhere on the built curve

        public struct Result { public int Pct; public double Cpu, Gpu, CpuRpm, GpuRpm, CpuSpeed; public double Hot => Math.Max(Cpu, Gpu); public double Rpm => CpuRpm + GpuRpm; }
        struct Sample { public DateTime T; public double Cpu, Gpu, CpuRpm, GpuRpm, CpuSpeed; }

        public readonly List<Result> Results = new List<Result>();
        readonly List<Sample> _samples = new List<Sample>();
        int _i;

        public bool Done => _i >= Levels.Length;
        public void StopDescending() { _samples.Clear(); _i = Levels.Length; }   // current level ran too hot: not recorded
        public int Level => Levels[Math.Min(_i, Levels.Length - 1)];
        public int Step => _i + 1;
        public double HeldSeconds => _samples.Count < 2 ? 0 : (_samples[_samples.Count - 1].T - _samples[0].T).TotalSeconds;

        // Feed one reading; moves on to the next level once temperatures have settled.
        public void Add(DateTime now, double cpu, double gpu, double cpuRpm, double gpuRpm, double cpuSpeed = 0)
        {
            if (Done) return;
            _samples.Add(new Sample { T = now, Cpu = cpu, Gpu = gpu, CpuRpm = cpuRpm, GpuRpm = gpuRpm, CpuSpeed = cpuSpeed });
            var held = HeldSeconds;
            if (held < MinHoldS) return;
            var last = _samples.Where(s => (now - s.T).TotalSeconds <= 30).ToList();
            var prev = _samples.Where(s => (now - s.T).TotalSeconds > 30 && (now - s.T).TotalSeconds <= 60).ToList();
            double Hot(List<Sample> l) => l.Average(s => Math.Max(s.Cpu, s.Gpu));
            bool settled = prev.Count > 0 && Math.Abs(Hot(last) - Hot(prev)) < 0.5;
            if (!settled && held < MaxHoldS) return;
            Results.Add(new Result
            {
                Pct = Level, Cpu = last.Average(s => s.Cpu), Gpu = last.Average(s => s.Gpu),
                CpuRpm = last.Average(s => s.CpuRpm), GpuRpm = last.Average(s => s.GpuRpm), CpuSpeed = last.Average(s => s.CpuSpeed),
            });
            _samples.Clear();
            _i++;
        }

        // Index of the sweet spot: the first level where the next step up isn't worth its extra rpm.
        public static int SweetSpot(IList<Result> r)
        {
            for (int i = 0; i + 1 < r.Count; i++)
            {
                double gain = Math.Max(Math.Max(r[i].Cpu - r[i + 1].Cpu, r[i].Gpu - r[i + 1].Gpu), (r[i + 1].CpuSpeed - r[i].CpuSpeed) / SpeedPctPerDeg);
                double addedRpm = Math.Max(1, r[i + 1].Rpm - r[i].Rpm);
                if (gain / (addedRpm / 1000) < WorthDegPer1000Rpm) return i;
            }
            return r.Count - 1;
        }

        // Curve that holds the sweet-spot fan speed at the temperature it settled at, eases in below it,
        // and climbs to 100% above it no faster than MaxSlope.
        public static string BuildCurve(IList<Result> results, out string summary)
        {
            var r = results.OrderBy(x => x.Pct).ToList();
            int k = SweetSpot(r);
            // Anchor on the chip whose temperature actually moves with the fans (a CPU pinned at its limit doesn't).
            bool cpuPinned = r.Max(x => x.Cpu) - r.Min(x => x.Cpu) < 2;
            double anchor = cpuPinned ? r[k].Gpu : r[k].Hot;
            int p = r[k].Pct, t = (int)Math.Round(Math.Min(85, Math.Max(55, anchor)));
            int lowPct = 25, midPct = (lowPct + p) / 2;
            int span = (int)Math.Ceiling((p - lowPct) / MaxSlope);       // degrees from lowPct to p at MaxSlope
            var pts = new List<(int, int)> { (t - span - 4, lowPct), (t - span / 2 - 2, midPct), (t, p) };
            if (p < 100) pts.Add((Math.Min(91, t + (int)Math.Ceiling((100 - p) / MaxSlope)), 100));
            summary = $"Sweet spot: {p}% fans, settling at CPU {r[k].Cpu:0}°C / GPU {r[k].Gpu:0}°C under this load."
                + (cpuPinned ? " The CPU stayed at its limit at every level, so more fan bought clock speed, not a cooler CPU." : "");
            return string.Join(",", pts.Select(x => $"{x.Item1}:{x.Item2}"));
        }

        public static void SelfTest()
        {
            // Big gains up to 70%, then 1°C for ~900 more rpm: sweet spot is 70%.
            var r = new[] { (40, 88.0, 2600.0), (55, 82.0, 3400.0), (70, 77.0, 4200.0), (85, 76.0, 5100.0), (100, 75.5, 6000.0) }
                .Select(x => new Result { Pct = x.Item1, Cpu = x.Item2, Gpu = x.Item2 - 8, CpuRpm = x.Item3, GpuRpm = x.Item3 }).ToList();
            if (SweetSpot(r) != 2) throw new Exception($"sweet spot {SweetSpot(r)}, expected 2");
            var pts = FanCurve.Parse(BuildCurve(r, out _));
            if (FanCurve.Interpolate(pts, 77) != 70) throw new Exception("curve should hold 70% at 77°C");
            for (int i = 1; i < pts.Length; i++)
                if ((pts[i].Item2 - pts[i - 1].Item2) / (pts[i].Item1 - pts[i - 1].Item1) > MaxSlope + 0.01)
                    throw new Exception("curve too steep at " + pts[i].Item1);

            // Kyle's PT315-52 run (CPU pinned at 92°C, GPU and clock follow the fans): sweet spot 70%.
            var kyle = new[] { (40, 92.1, 82.2, 3588, 3521, 106), (55, 92.1, 80.5, 3700, 3886, 112), (70, 91.9, 76.9, 4415, 4663, 121),
                               (85, 91.8, 74.1, 5113, 5383, 126), (100, 91.7, 68.7, 5740, 6073, 129) }
                .Select(x => new Result { Pct = x.Item1, Cpu = x.Item2, Gpu = x.Item3, CpuRpm = x.Item4, GpuRpm = x.Item5, CpuSpeed = x.Item6 }).ToList();
            if (SweetSpot(kyle) != 2) throw new Exception($"Kyle's run: sweet spot {SweetSpot(kyle)}, expected 2");
            if (FanCurve.Interpolate(FanCurve.Parse(BuildCurve(kyle, out _)), 77) != 70) throw new Exception("Kyle's run: curve should hold 70% at the GPU's 77°C");

            var tune = new SmartTune(); var t0 = DateTime.Now;
            for (int s = 0; s <= MaxHoldS; s += 2) tune.Add(t0.AddSeconds(s), 70, 60, 3000, 3000);
            if (tune.Step != 2) throw new Exception("steady temps should finish a level after MinHoldS");
        }
    }
}
