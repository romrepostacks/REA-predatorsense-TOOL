using System;
using System.Linq;
using System.Management;
using System.Windows.Media;

namespace ReaSense
{
    public enum FanMode { Auto = 1, Max = 2, Custom = 3 }

    // Acer's gaming WMI interface (root\WMI AcerGamingFunction, GUID 7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56).
    // Bit layouts follow the Linux acer-wmi driver. Every output carries a status in its low byte; 0 = success.
    public static class Acer
    {
        static readonly object Lock = new object();
        static ManagementObject _obj;

        static ManagementObject Obj => _obj ?? (_obj = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AcerGamingFunction")
            .Get().Cast<ManagementObject>().First());

        public static bool HasMethod(string name)
        {
            try
            {
                using (var cls = new ManagementClass(@"root\WMI", "AcerGamingFunction", null))
                    return cls.Methods.Cast<MethodData>().Any(m => m.Name == name);
            }
            catch { return false; }
        }

        // One method call with an integer input (or none). Input width is read from the class.
        public static ulong Call(string method, ulong input = 0)
        {
            lock (Lock)
            {
                var inp = Obj.GetMethodParameters(method);
                if (inp != null)
                    inp["gmInput"] = inp.Properties["gmInput"].Type == CimType.UInt32 ? (object)(uint)input : input;
                var o = Obj.InvokeMethod(method, inp, null)?["gmOutput"];
                if (o is byte[] b)
                {
                    var buf = new byte[8];
                    Array.Copy(b, buf, Math.Min(8, b.Length));
                    return BitConverter.ToUInt64(buf, 0);
                }
                return o == null ? 0 : Convert.ToUInt64(o);
            }
        }

        static void Check(ulong r, string what)
        {
            if ((r & 0xFF) != 0) throw new InvalidOperationException($"{what} failed (status 0x{r & 0xFF:X2})");
        }

        // ---- Sensors ----
        public const int CpuTemp = 0x01, CpuFanRpm = 0x02, GpuFanRpm = 0x06, GpuTemp = 0x0A;

        public static int? Sensor(int id)
        {
            var r = Call("GetGamingSysInfo", 0x1UL | (ulong)id << 8);
            return (r & 0xFF) != 0 ? (int?)null : (int)((r >> 8) & 0xFFFF);
        }

        // ---- Fans: CPU is behavior bit 0 / speed id 1, GPU is behavior bit 3 / speed id 4 ----
        public static void SetFanMode(FanMode mode)
        {
            ulong m = (ulong)mode;
            Check(Call("SetGamingFanBehavior", 0x9UL | m << 16 | m << 22), "SetGamingFanBehavior");
        }

        public static void SetFanSpeed(bool cpu, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            Check(Call("SetGamingFanSpeed", (cpu ? 1UL : 4UL) | (ulong)percent << 8), "SetGamingFanSpeed");
        }

        // ---- GPU overclock presets: misc setting 5 (PredatorSense: Normal 0, Faster 1, Turbo 2). ----
        // Misc setting 7 is the CPU overclock level in PredatorSense, so it is never written here.
        public static int? GetGpuOc()
        {
            var r = Call("GetGamingMiscSetting", 0x05);
            return (r & 0xFF) != 0 ? (int?)null : (int)((r >> 8) & 0xFF);
        }

        public static void SetGpuOc(int level)
        {
            Check(Call("SetGamingMiscSetting", 0x05UL | (ulong)level << 8), "SetGamingMiscSetting");
        }

        // ---- Keyboard, 4 zones left to right ----
        // Static zone colors: zone bit | R<<8 | G<<16 | B<<24.
        public static void SetZoneColor(int zone, Color c)
        {
            Check(Call("SetGamingRgbKb", 1UL << zone | (ulong)c.R << 8 | (ulong)c.G << 16 | (ulong)c.B << 24), "SetGamingRgbKb");
        }

        // Effect packet as PredatorSense 3.00 builds it for the 4-zone keyboard:
        // [mode, speed 1-9, brightness 0-100, flag, direction, R, G, B, 3, 1]; Static is [0, 0, brightness, 0 ... 0, 1].
        // The BIOS method takes a 16-byte array (10 gives "Invalid parameter"), so the rest is zero.
        // Flag 8 marks Wave; direction (1 or 2) is used by Wave and Shifting only.
        public static byte[] EffectPacket(int mode, int speed, int brightness, int direction, Color c)
        {
            var b = new byte[16];
            b[2] = (byte)Math.Max(0, Math.Min(100, brightness));
            b[9] = 1;
            if (mode == 0) return b;
            b[0] = (byte)mode;
            b[1] = (byte)Math.Max(1, Math.Min(9, speed));
            b[3] = (byte)(mode == 3 ? 8 : 0);
            b[4] = (byte)(mode == 3 || mode == 4 ? direction : 0);
            b[5] = c.R; b[6] = c.G; b[7] = c.B;
            b[8] = 3;
            return b;
        }

        public static void SetKeyboardEffect(byte[] packet)
        {
            lock (Lock)
            {
                var inp = Obj.GetMethodParameters("SetGamingKBBacklight");
                inp["gmInput"] = packet;
                var r = Convert.ToUInt64(Obj.InvokeMethod("SetGamingKBBacklight", inp, null)?["gmOutput"] ?? 0UL);
                Check(r, "SetGamingKBBacklight");
            }
        }

        public static bool KeyboardReadable()
        {
            lock (Lock)
            {
                var r = Obj.InvokeMethod("GetGamingKBBacklight", null, null);
                return r != null && Convert.ToInt32(r["gmReturn"]) == 0;
            }
        }
    }

    // Read-only check of what this laptop supports. Makes only Get calls.
    public class Support
    {
        // Models where writes have been confirmed on real hardware. Others start read-only.
        public static readonly string[] TestedModels = { "Predator PT315-52" };

        public string Model = "";
        public bool Tested, Wmi, Sensors, Fans, GpuOc, Keyboard;

        public static Support Detect()
        {
            var s = new Support();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                    s.Model = q.Get().Cast<ManagementObject>().First()["Model"]?.ToString()?.Trim() ?? "";
            }
            catch { }
            s.Tested = TestedModels.Contains(s.Model);
            try { s.Wmi = Acer.HasMethod("GetGamingSysInfo"); } catch { }
            if (!s.Wmi) return s;
            try { s.Sensors = Acer.Sensor(Acer.CpuTemp) != null && Acer.Sensor(Acer.GpuTemp) != null; } catch { }
            try
            {
                s.Fans = s.Sensors && Acer.HasMethod("SetGamingFanBehavior") && Acer.HasMethod("SetGamingFanSpeed")
                    && (Acer.Call("GetGamingFanBehavior", 0x9) & 0xFF) == 0;
            }
            catch { }
            try { s.GpuOc = Acer.GetGpuOc() != null; } catch { }
            try { s.Keyboard = Acer.HasMethod("SetGamingKBBacklight") && Acer.HasMethod("SetGamingRgbKb") && Acer.KeyboardReadable(); } catch { }
            return s;
        }
    }
}
