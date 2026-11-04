using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TalonForge
{
    // A timed benchmark under the user's own load (plus optional CPU load). Leaves the fans in whatever mode
    // the user picked, records every 2 s tick, and saves one summary row to benchmarks.csv so runs can be compared.
    public class BenchRun
    {
        public const double WarmupFraction = 0.2;   // averages skip the first 20%, while temperatures are still climbing
        public static readonly string Path = System.IO.Path.Combine(FanLog.Dir, "benchmarks.csv");
        const string Header = "time,label,mode,curve,minutes,cpu_avg,cpu_peak,gpu_avg,gpu_peak,cpu_rpm_avg,gpu_rpm_avg,cpu_speed_pct_avg,gpu_mhz_avg,gpu_watts_avg";

        public readonly DateTime Start;
        public readonly int Seconds;
        public readonly string Mode, Curve, Label;
        readonly List<double?[]> _rows = new List<double?[]>();   // cpu, gpu, cpuRpm, gpuRpm, cpuSpeed, gpuMhz, gpuWatts

        public BenchRun(int seconds, string mode, string curve, string label, DateTime? start = null)
        {
            Seconds = seconds; Mode = mode; Curve = curve ?? ""; Label = label ?? ""; Start = start ?? DateTime.Now;
        }

        public double Elapsed(DateTime now) => (now - Start).TotalSeconds;
        public bool Done(DateTime now) => Elapsed(now) >= Seconds;
        public int Count => _rows.Count;

        public void Add(int? cpu, int? gpu, int? cpuRpm, int? gpuRpm, int? cpuSpeed, int? gpuMhz, double? gpuWatts) =>
            _rows.Add(new double?[] { cpu, gpu, cpuRpm, gpuRpm, cpuSpeed, gpuMhz, gpuWatts });

        double? Avg(int col)
        {
            var v = _rows.Skip((int)(_rows.Count * WarmupFraction)).Select(r => r[col]).Where(x => x.HasValue && x > 0).ToList();
            return v.Count == 0 ? (double?)null : Math.Round(v.Average(x => x.Value), 1);
        }

        double? Peak(int col)
        {
            var v = _rows.Select(r => r[col]).Where(x => x.HasValue).ToList();
            return v.Count == 0 ? (double?)null : v.Max();
        }

        public string Summary() =>
            $"CPU avg {Avg(0):0}°C, peak {Peak(0):0}°C.  GPU avg {Avg(1):0}°C, peak {Peak(1):0}°C.  " +
            $"Fans {Avg(2):0} / {Avg(3):0} rpm.  CPU speed {Avg(4):0}% of base" + (Avg(5) is double m ? $", GPU {m:0} MHz" : "") +
            (Avg(6) is double w ? $" at {w:0.#} W." : ".");

        static string F(double? x) => x?.ToString(CultureInfo.InvariantCulture) ?? "";
        static string Clean(string s) => (s ?? "").Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        public string CsvLine() => string.Join(",", new[]
        {
            Start.ToString("yyyy-MM-dd HH:mm"), Clean(Label), Mode, Clean(Curve), (Seconds / 60).ToString(),
            F(Avg(0)), F(Peak(0)), F(Avg(1)), F(Peak(1)), F(Avg(2)), F(Avg(3)), F(Avg(4)), F(Avg(5)), F(Avg(6)),
        });

        public void Save()
        {
            Directory.CreateDirectory(FanLog.Dir);
            if (!File.Exists(Path)) File.WriteAllText(Path, Header + "\r\n");
            File.AppendAllText(Path, CsvLine() + "\r\n");
        }

        // The last few saved runs as an aligned table, newest first.
        public static string History(int count = 10)
        {
            if (!File.Exists(Path)) return "";
            var rows = File.ReadLines(Path).Skip(1).Select(l => l.Split(',')).Where(c => c.Length >= 9).Reverse().Take(count).ToList();
            if (rows.Count == 0) return "";
            string label(string[] c) => (c[1] != "" ? c[1] : c[2] + (c[3] != "" ? " " + c[3] : ""));
            var head = $"{"When",-17} {"Min",3}  {"CPU avg/peak",12}  {"GPU avg/peak",12}  Run";
            return head + "\n" + string.Join("\n", rows.Select(c =>
                $"{c[0],-17} {c[4],3}  {Num(c[5]) + "/" + Num(c[6]) + "°C",12}  {Num(c[7]) + "/" + Num(c[8]) + "°C",12}  {label(c)}"));
        }

        static string Num(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0") : "--";

        public static void SelfTest()
        {
            var t0 = new DateTime(2026, 1, 1);
            var b = new BenchRun(600, "Curve", "45:25,80:100", "after, repaste", t0);
            if (b.Done(t0.AddSeconds(599)) || !b.Done(t0.AddSeconds(600))) throw new Exception("bench duration");
            for (int i = 0; i < 10; i++) b.Add(i < 2 ? 50 : 80, null, 3000, 0, 120, null, null);   // two warm-up rows, a 0 rpm miss
            b.Add(95, null, 3000, 0, 120, null, null);
            var cols = b.CsvLine().Split(',');
            if (cols.Length != Header.Split(',').Length) throw new Exception("bench csv column count: " + b.CsvLine());
            if (cols[1] != "after  repaste" || cols[3] != "45:25 80:100") throw new Exception("bench csv commas not removed");
            if (cols[5] != "81.7" || cols[6] != "95") throw new Exception($"bench cpu avg/peak {cols[5]}/{cols[6]}");
            if (cols[7] != "" || cols[10] != "") throw new Exception("missing readings should stay empty");
        }
    }
}
