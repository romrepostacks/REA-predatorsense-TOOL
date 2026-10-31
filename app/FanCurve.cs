using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReaSense
{
    // Temperature -> fan % curve with smoothing, so short CPU spikes don't make the fans surge.
    public class FanCurve
    {
        public const double Smooth = 0.25;   // weight of the newest reading per tick (about 8 s to settle at 2 s ticks)
        public const int StepUp = 10;        // most a fan rises per tick, in %
        public const int StepDown = 3;       // most a fan falls per tick, in %

        public readonly (double Temp, int Percent)[] Points;
        double? _avg;
        int _last = 100;

        public FanCurve(string text) => Points = Parse(text);

        // "tempC:percent,..." e.g. "45:25,70:65,80:100". Throws on bad input.
        public static (double, int)[] Parse(string text)
        {
            var pts = (text ?? "").Split(',').Select(p =>
            {
                var m = Regex.Match(p, @"^\s*(\d+(?:\.\d+)?)\s*:\s*(\d+)\s*$");
                if (!m.Success || int.Parse(m.Groups[2].Value) > 100)
                    throw new FormatException($"Bad curve point '{p.Trim()}'. Use tempC:percent, e.g. 70:65");
                return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value));
            }).OrderBy(p => p.Item1).ToArray();
            return pts;
        }

        public static int Interpolate((double Temp, int Percent)[] pts, double t)
        {
            if (t <= pts[0].Temp) return pts[0].Percent;
            for (int i = 1; i < pts.Length; i++)
                if (t <= pts[i].Temp)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    return (int)Math.Round(a.Percent + (b.Percent - a.Percent) * (t - a.Temp) / (b.Temp - a.Temp));
                }
            return pts[pts.Length - 1].Percent;
        }

        // Next fan % for this reading: curve on an averaged temperature, rate-limited.
        public int Next(double temp)
        {
            _avg = _avg == null ? temp : _avg + Smooth * (temp - _avg.Value);
            var target = Interpolate(Points, _avg.Value);
            _last = Math.Min(Math.Max(target, _last - StepDown), _last + StepUp);
            return _last;
        }

        // Run with "ReaSense.exe --selftest"; exits non-zero on failure.
        public static void SelfTest()
        {
            var p = Parse("70:60,50:30,80:80");
            foreach (var (t, want) in new[] { (20.0, 30), (50.0, 30), (60.0, 45), (70.0, 60), (75.0, 70), (80.0, 80), (99.0, 80) })
                if (Interpolate(p, t) != want) throw new Exception($"curve({t}) = {Interpolate(p, t)}, expected {want}");
            foreach (var bad in new[] { "70:101", "70", "x:50", "" })
            {
                try { Parse(bad); throw new Exception($"'{bad}' should be rejected"); }
                catch (FormatException) { }
            }
            var c = new FanCurve("50:20,80:100");
            if (c.Next(50) != 100 - StepDown) throw new Exception("fans should ramp down slowly from 100%");
        }
    }
}
