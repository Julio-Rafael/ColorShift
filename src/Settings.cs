using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace ColorShift
{
    public class Profile
    {
        public string Name;
        public string ExePath;
        public int Level = 75;
    }

    public class AppSettings
    {
        public int DesktopLevel = 50;
        public bool AllMonitors;
        public List<Profile> Profiles = new List<Profile>();
        public CrosshairSettings Crosshair = new CrosshairSettings();
        public string ThemeMode = "Sistema";
        public RamLedSettings RamLeds = new RamLedSettings();

        static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ColorShift"); }
        }

        static string FilePath
        {
            get { return Path.Combine(Folder, "settings.xml"); }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var stream = File.OpenRead(FilePath))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(stream);
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            using (var stream = File.Create(temp))
                new XmlSerializer(typeof(AppSettings)).Serialize(stream, this);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temp, FilePath);
        }

        internal Profile FindProfile(ForegroundInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ExePath)) return null;
            foreach (var p in Profiles)
                if (string.Equals(p.ExePath, info.ExePath, StringComparison.OrdinalIgnoreCase)) return p;
            // Fallback pelo nome do executável: o jogo pode ter sido atualizado/movido de pasta.
            foreach (var p in Profiles)
                if (string.Equals(Path.GetFileNameWithoutExtension(p.ExePath), info.ExeName, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Importa perfis de %APPDATA%\vibranceGUI. Retorna quantos perfis foram adicionados.</summary>
        internal int ImportVibranceGui(IColorController controller)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibranceGUI");
            string xml = Path.Combine(folder, "applicationData.xml");
            if (!File.Exists(xml)) throw new FileNotFoundException("Configuração do vibranceGUI não encontrada.", xml);

            var doc = new XmlDocument();
            doc.Load(xml);
            int added = 0;
            foreach (XmlNode node in doc.SelectNodes("/ArrayOfApplicationSetting/ApplicationSetting"))
            {
                string name = Text(node, "Name");
                string path = Text(node, "FileName");
                int raw;
                if (string.IsNullOrEmpty(path) || !int.TryParse(Text(node, "IngameLevel"), out raw)) continue;
                if (Profiles.Exists(p => string.Equals(p.ExePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                Profiles.Add(new Profile { Name = name, ExePath = path, Level = controller.LegacyRawToPercent(raw) });
                added++;
            }

            int inactive;
            string ini = Path.Combine(folder, "vibranceGUI.ini");
            if (File.Exists(ini))
                foreach (string line in File.ReadAllLines(ini))
                    if (line.StartsWith("inactiveValue=") && int.TryParse(line.Substring(14), out inactive))
                        DesktopLevel = controller.LegacyRawToPercent(inactive);
            return added;
        }

        static string Text(XmlNode node, string child)
        {
            XmlNode n = node.SelectSingleNode(child);
            return n == null ? null : n.InnerText;
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "ColorShift";

        static string Command
        {
            get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --minimized"; }
        }

        public static bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(ValueName) != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(ValueName, Command);
                    else key.DeleteValue(ValueName, false);
                }
            }
        }

        /// <summary>Mantém o caminho correto caso o executável tenha sido movido.</summary>
        public static void RefreshPath()
        {
            if (Enabled) Enabled = true;
        }
    }
}
