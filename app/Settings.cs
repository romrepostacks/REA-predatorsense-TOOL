using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TalonForge
{
    // Stored as JSON in %APPDATA%\TalonForge\settings.json.
    [DataContract]
    public class Settings
    {
        [DataMember] public string FanMode;
        [DataMember] public string Curve, GpuCurve;   // Curve drives the CPU fan, or both fans when LinkCurves
        [DataMember] public bool LinkCurves;
        [DataMember] public int CpuPercent, GpuPercent;
        [DataMember] public bool AllowUntested, HideDisclaimer;
        [DataMember] public string Effect;
        [DataMember] public int Brightness, Speed, Direction;
        [DataMember] public string[] ZoneColors;

        public Settings() => Defaults(default);

        // DataContract skips constructors, so members missing from an older file get defaults here.
        [OnDeserializing]
        void Defaults(StreamingContext _)
        {
            FanMode = "Auto";
            Curve = "45:25,55:35,63:50,70:65,75:85,80:100";
            GpuCurve = null;      // null = start from Curve
            LinkCurves = true;
            CpuPercent = GpuPercent = 60;
            Effect = "Static";
            Brightness = 100; Speed = 4; Direction = 1;
            ZoneColors = new[] { "#00AEC7", "#00AEC7", "#00AEC7", "#00AEC7" };
        }

        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TalonForge", "settings.json");
        static readonly DataContractJsonSerializer Json = new DataContractJsonSerializer(typeof(Settings));

        public static Settings Load()
        {
            try
            {
                using (var f = File.OpenRead(FilePath)) return (Settings)Json.ReadObject(f);
            }
            catch { return new Settings(); }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            using (var f = File.Create(FilePath)) Json.WriteObject(f, this);
        }
    }
}
