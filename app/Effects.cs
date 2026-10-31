using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace TalonForge
{
    public class Effect
    {
        public string Name, Description;
        public int? Native;            // keyboard firmware effect number; null = drawn by the app
        public bool UsesZoneColors, UsesDirection;

        public Effect(string name, int? native, string description, bool zoneColors = false, bool direction = false)
        {
            Name = name; Native = native; Description = description; UsesZoneColors = zoneColors; UsesDirection = direction;
        }
    }

    public static class Effects
    {
        public static readonly Effect[] All =
        {
            new Effect("Static", 0, "Your four zone colors, steady.", zoneColors: true),
            new Effect("Breathing", 1, "Zone 1's color fades in and out.", zoneColors: true),
            new Effect("Neon", 2, "The whole keyboard cycles through every color."),
            new Effect("Wave", 3, "A rainbow rolls across the keyboard.", direction: true),
            new Effect("Shifting", 4, "Zone 1's color slides across.", zoneColors: true, direction: true),
            new Effect("Zoom", 5, "Zone 1's color pulses out from the center.", zoneColors: true),
            new Effect("Meteor", 6, "A bright streak with a fading tail.", zoneColors: true),
            new Effect("Twinkling", 7, "Zones sparkle at random.", zoneColors: true),
            // Drawn by the app: it sets static zone colors several times a second, so they stop when the app closes.
            new Effect("Rainbow Flow", null, "A smooth rainbow drawn by the app.", direction: true),
            new Effect("Temperature", null, "Blue when the CPU is cool, red when it's hot."),
            new Effect("Color Cycle", null, "Your four zone colors rotate around the keyboard.", zoneColors: true),
            new Effect("Pulse", null, "Your four zone colors breathe together.", zoneColors: true),
        };

        public static Effect Find(string name) => All.FirstOrDefault(e => e.Name == name) ?? All[0];

        // One frame of an effect: used for the previews, and to drive the app-drawn effects.
        // Firmware effects are approximations of what the keyboard does. Direction 1 = right to left.
        public static Color[] Frame(Effect e, double t, Color[] zones, int speed, int direction, double cpuTemp)
        {
            double f = 0.08 * Math.Max(1, speed);              // cycles per second
            double phase = t * f % 1.0;
            int sign = direction == 2 ? 1 : -1;
            Color c0 = zones[0];
            var o = new Color[4];
            for (int i = 0; i < 4; i++)
            {
                double pos = sign > 0 ? i : 3 - i;              // position along the direction of travel
                switch (e.Name)
                {
                    case "Static": o[i] = zones[i]; break;
                    case "Breathing": o[i] = Scale(c0, (1 - Math.Cos(2 * Math.PI * phase)) / 2); break;
                    case "Neon": o[i] = Hue(phase * 360); break;
                    case "Wave": o[i] = Hue(phase * 360 - pos * 60); break;
                    case "Shifting": o[i] = Scale(c0, Math.Max(0, 1 - Dist(phase * 4, pos, 4))); break;
                    case "Zoom": o[i] = Scale(c0, Math.Max(0, 1 - Math.Abs(phase * 2 - (i == 1 || i == 2 ? 0.25 : 1)) * 1.5)); break;
                    case "Meteor": { double d = (phase * 6 - pos + 6) % 6; o[i] = Scale(c0, d < 3 ? Math.Pow(1 - d / 3, 2) : 0); break; }
                    case "Twinkling": o[i] = Scale(zones[i], Math.Max(0, Math.Sin(t * f * 9 * (1 + i * 0.37) + i * 1.7))); break;
                    case "Rainbow Flow": o[i] = Hue(phase * 360 + pos * 45); break;
                    case "Temperature": o[i] = Hue(220 - 220 * Clamp01((cpuTemp - 45) / 45)); break;
                    case "Color Cycle":
                    {
                        double s = t * f * 2; int k = (int)Math.Floor(s);
                        o[i] = Mix(zones[(i + k) % 4], zones[(i + k + 1) % 4], Smoothstep(s - k));
                        break;
                    }
                    case "Pulse": o[i] = Scale(zones[i], 0.25 + 0.75 * (1 - Math.Cos(2 * Math.PI * phase)) / 2); break;
                    default: o[i] = zones[i]; break;
                }
            }
            return o;
        }

        static double Clamp01(double x) => Math.Max(0, Math.Min(1, x));
        static double Smoothstep(double x) => x * x * (3 - 2 * x);
        static double Dist(double a, double b, double n) { double d = Math.Abs(a - b) % n; return Math.Min(d, n - d); }
        static Color Scale(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        static Color Mix(Color a, Color b, double k) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

        public static Color Hue(double h)
        {
            h = ((h % 360) + 360) % 360 / 60;
            double x = 1 - Math.Abs(h % 2 - 1);
            double r = 0, g = 0, b = 0;
            switch ((int)h)
            {
                case 0: r = 1; g = x; break;
                case 1: r = x; g = 1; break;
                case 2: g = 1; b = x; break;
                case 3: g = x; b = 1; break;
                case 4: r = x; b = 1; break;
                default: r = 1; b = x; break;
            }
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }

    // Applies an effect to the keyboard. App-drawn effects run on a timer that only writes zones whose color changed.
    public class Lighting
    {
        // ponytail: 5 fps keeps WMI traffic low; raise once we know the EC doesn't persist each write.
        const int FrameMs = 200;

        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        readonly Color[] _sent = new Color[4];
        DateTime _start;
        Effect _effect;
        Color[] _zones;
        int _speed, _direction;
        public Func<double> CpuTemp = () => 50;
        public event Action<string> Failed;

        public Lighting() => _timer.Tick += (s, e) => Draw();

        public void Apply(Effect effect, Color[] zones, int brightness, int speed, int direction)
        {
            _timer.Stop();
            _effect = effect; _zones = zones; _speed = speed; _direction = direction;
            if (effect.Native is int mode)
            {
                if (mode == 0) for (int z = 0; z < 4; z++) Acer.SetZoneColor(z, zones[z]);
                Acer.SetKeyboardEffect(Acer.EffectPacket(mode, speed, brightness, direction, zones[0]));
                return;
            }
            Acer.SetKeyboardEffect(Acer.EffectPacket(0, 0, brightness, 0, zones[0]));
            for (int z = 0; z < 4; z++) _sent[z] = Color.FromArgb(0, 0, 0, 0);   // force the first write
            _start = DateTime.Now;
            Draw();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        void Draw()
        {
            try
            {
                var frame = Effects.Frame(_effect, (DateTime.Now - _start).TotalSeconds, _zones, _speed, _direction, CpuTemp());
                for (int z = 0; z < 4; z++)
                    if (frame[z] != _sent[z]) { Acer.SetZoneColor(z, frame[z]); _sent[z] = frame[z]; }
            }
            catch (Exception ex)
            {
                _timer.Stop();
                Failed?.Invoke(ex.Message);
            }
        }
    }
}
