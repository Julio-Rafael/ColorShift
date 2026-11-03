using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ColorShift
{
    /// <summary>Códigos VCP do padrão VESA MCCS usados pelo ColorShift (ver docs/ANALISE.md).</summary>
    static class Vcp
    {
        public const byte FactoryDefaults = 0x04;
        public const byte FactoryColorDefaults = 0x08;
        public const byte Brightness = 0x10;
        public const byte Contrast = 0x12;
        public const byte ColorPreset = 0x14;
        public const byte RedGain = 0x16;
        public const byte GreenGain = 0x18;
        public const byte BlueGain = 0x1A;
        public const byte Sharpness = 0x87;
        public const byte Saturation = 0x8A;

        public const int PresetUser1 = 0x0B;

        public static string PresetName(int value)
        {
            switch (value)
            {
                case 0x01: return "sRGB";
                case 0x02: return "Nativo";
                case 0x03: return "4000 K";
                case 0x04: return "5000 K";
                case 0x05: return "6500 K (neutro)";
                case 0x06: return "7500 K";
                case 0x07: return "8200 K";
                case 0x08: return "9300 K (frio)";
                case 0x09: return "10000 K";
                case 0x0A: return "11500 K";
                case 0x0B: return "Usuário (RGB manual)";
                case 0x0C: return "Usuário 2";
                case 0x0D: return "Usuário 3";
                default: return "Modo 0x" + value.ToString("X2");
            }
        }
    }

    /// <summary>
    /// Um monitor físico controlado por DDC/CI (dxva2.dll), o mesmo canal que o software do fabricante usa.
    /// As funções disponíveis vêm da string de capacidades que o próprio monitor informa.
    /// </summary>
    sealed class DdcMonitor : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitor(IntPtr handle);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, StringBuilder caps, uint length);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);

        // A string de capacidades leva segundos para chegar pelo DDC/CI e não muda; guarda por monitor.
        static readonly Dictionary<string, string> CapsCache = new Dictionary<string, string>();

        IntPtr _handle;
        readonly Dictionary<byte, List<int>> _vcp = new Dictionary<byte, List<int>>();

        public string Device { get; private set; }
        public string Model { get; private set; }
        public string MccsVersion { get; private set; }

        DdcMonitor(IntPtr handle, string device, string description)
        {
            _handle = handle;
            Device = device;
            Model = description;
            ReadCapabilities();
        }

        public override string ToString()
        {
            return Device.Replace(@"\\.\", "") + " — " + Model;
        }

        public static List<DdcMonitor> EnumerateAll()
        {
            var list = new List<DdcMonitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hmon, hdc, rect, data) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                GetMonitorInfo(hmon, ref info);
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out count) || count == 0) return true;
                var physical = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, physical)) return true;
                foreach (var p in physical)
                {
                    var monitor = new DdcMonitor(p.Handle, info.Device, p.Description);
                    if (monitor._vcp.Count > 0) list.Add(monitor);
                    else monitor.Dispose();
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        void ReadCapabilities()
        {
            string key = Device + "|" + Model;
            string caps;
            lock (CapsCache)
                CapsCache.TryGetValue(key, out caps);
            if (caps == null)
            {
                uint length;
                if (!GetCapabilitiesStringLength(_handle, out length) || length == 0) return;
                var sb = new StringBuilder((int)length);
                if (!CapabilitiesRequestAndCapabilitiesReply(_handle, sb, length)) return;
                caps = sb.ToString();
                if (caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase) >= 0)
                    lock (CapsCache) CapsCache[key] = caps;
            }

            Match model = Regex.Match(caps, @"model\(([^)]*)\)", RegexOptions.IgnoreCase);
            if (model.Success) Model = model.Groups[1].Value.Trim();
            Match mccs = Regex.Match(caps, @"mccs_ver\(([^)]*)\)", RegexOptions.IgnoreCase);
            if (mccs.Success) MccsVersion = mccs.Groups[1].Value.Trim();

            int start = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (start >= 0) ParseVcpList(caps, start + 4);
        }

        /// <summary>
        /// Lê a lista "vcp(...)". Os códigos podem vir colados ("0210") ou separados, e cada um pode trazer
        /// a lista de valores aceitos entre parênteses, como "14(01 05 0B)".
        /// </summary>
        void ParseVcpList(string caps, int i)
        {
            int lastCode = -1;
            while (i < caps.Length)
            {
                char c = caps[i];
                if (c == ')') return;
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(')
                {
                    int depth = 1;
                    var hex = new StringBuilder();
                    for (i++; i < caps.Length && depth > 0; i++)
                    {
                        if (caps[i] == '(') depth++;
                        else if (caps[i] == ')') depth--;
                        else if (depth == 1 && Uri.IsHexDigit(caps[i])) hex.Append(caps[i]);
                    }
                    if (lastCode >= 0)
                        for (int k = 0; k + 1 < hex.Length; k += 2)
                            _vcp[(byte)lastCode].Add(Convert.ToInt32(hex.ToString(k, 2), 16));
                    continue;
                }
                if (i + 1 < caps.Length && Uri.IsHexDigit(c) && Uri.IsHexDigit(caps[i + 1]))
                {
                    lastCode = Convert.ToInt32(caps.Substring(i, 2), 16);
                    if (!_vcp.ContainsKey((byte)lastCode)) _vcp[(byte)lastCode] = new List<int>();
                    i += 2;
                    continue;
                }
                i++;
            }
        }

        public bool Supports(byte code)
        {
            return _vcp.ContainsKey(code);
        }

        /// <summary>Valores aceitos por um código de lista (ex.: predefinições de cor). Vazio para controles contínuos.</summary>
        public IList<int> ValuesOf(byte code)
        {
            List<int> values;
            return _vcp.TryGetValue(code, out values) ? values : new List<int>();
        }

        public bool TryGet(byte code, out int current, out int maximum)
        {
            uint cur = 0, max = 0;
            bool ok = _handle != IntPtr.Zero && GetVCPFeatureAndVCPFeatureReply(_handle, code, IntPtr.Zero, out cur, out max);
            current = ok ? (int)cur : 0;
            maximum = ok ? (int)max : 0;
            return ok;
        }

        public bool Set(byte code, int value)
        {
            return _handle != IntPtr.Zero && SetVCPFeature(_handle, code, (uint)value);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            DestroyPhysicalMonitor(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
