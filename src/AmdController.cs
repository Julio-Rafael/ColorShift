using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ColorShift
{
    /// <summary>
    /// Controle de saturação pela AMD Display Library (ADL), usando as mesmas funções
    /// que o vibranceGUI importa de atiadlxx.dll (ver docs/ANALISE.md).
    /// </summary>
    sealed class AmdController : IColorController
    {
        const string Dll = "atiadlxx.dll";
        const int AdlOk = 0;
        const int AdlDisplayColorSaturation = 4;
        const int DisplayConnected = 1;
        const int DisplayMapped = 2;

        delegate IntPtr AdlMallocCallback(int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct AdlAdapterInfo
        {
            public int Size;
            public int AdapterIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string UDID;
            public int BusNumber;
            public int DeviceNumber;
            public int FunctionNumber;
            public int VendorID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AdapterName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
            public int Present;
            public int Exist;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string PNPString;
            public int OSDisplayIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AdlDisplayId
        {
            public int LogicalIndex;
            public int PhysicalIndex;
            public int LogicalAdapterIndex;
            public int PhysicalAdapterIndex;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct AdlDisplayInfo
        {
            public AdlDisplayId DisplayId;
            public int ControllerIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ManufacturerName;
            public int DisplayType;
            public int OutputType;
            public int Connector;
            public int InfoMask;
            public int InfoValue;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Main_Control_Create(AdlMallocCallback callback, int enumConnectedAdapters);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Main_Control_Destroy();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Adapter_NumberOfAdapters_Get(out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Adapter_AdapterInfo_Get(IntPtr info, int size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Adapter_Active_Get(int adapterIndex, out int status);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Display_DisplayInfo_Get(int adapterIndex, out int count, out IntPtr info, int forceDetect);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Display_Color_Get(int adapterIndex, int displayIndex, int colorType, out int current, out int def, out int min, out int max, out int step);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int ADL_Display_Color_Set(int adapterIndex, int displayIndex, int colorType, int current);

        struct Target
        {
            public int Adapter;
            public int Display;
        }

        // O ADL guarda o ponteiro do callback; manter a referência evita que o GC o colete.
        static readonly AdlMallocCallback Malloc = size => Marshal.AllocCoTaskMem(size);

        readonly Dictionary<string, Target> _targets = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _displays = new List<string>();
        readonly Dictionary<string, int> _original = new Dictionary<string, int>();
        string _adapterName = "AMD";
        bool _disposed;

        public AmdController()
        {
            int status = ADL_Main_Control_Create(Malloc, 1);
            if (status != AdlOk)
                throw new InvalidOperationException("ADL_Main_Control_Create falhou (status " + status + ").");
            Refresh();
        }

        public string Description { get { return _adapterName; } }
        public int MinPercent { get { return 0; } }
        public IList<string> Displays { get { return _displays.AsReadOnly(); } }

        public void Refresh()
        {
            _targets.Clear();
            _displays.Clear();

            int count;
            if (ADL_Adapter_NumberOfAdapters_Get(out count) != AdlOk || count < 1) return;

            int size = Marshal.SizeOf(typeof(AdlAdapterInfo));
            IntPtr buffer = Marshal.AllocCoTaskMem(size * count);
            try
            {
                if (ADL_Adapter_AdapterInfo_Get(buffer, size * count) != AdlOk) return;
                for (int i = 0; i < count; i++)
                {
                    var adapter = (AdlAdapterInfo)Marshal.PtrToStructure(new IntPtr(buffer.ToInt64() + (long)i * size), typeof(AdlAdapterInfo));
                    int active;
                    if (adapter.Present == 0 || ADL_Adapter_Active_Get(adapter.AdapterIndex, out active) != AdlOk || active == 0)
                        continue;
                    if (string.IsNullOrEmpty(adapter.DisplayName) || _targets.ContainsKey(adapter.DisplayName))
                        continue;
                    AddDisplays(adapter);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        void AddDisplays(AdlAdapterInfo adapter)
        {
            int count;
            IntPtr info;
            if (ADL_Display_DisplayInfo_Get(adapter.AdapterIndex, out count, out info, 0) != AdlOk || info == IntPtr.Zero)
                return;
            try
            {
                int size = Marshal.SizeOf(typeof(AdlDisplayInfo));
                for (int i = 0; i < count; i++)
                {
                    var display = (AdlDisplayInfo)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + (long)i * size), typeof(AdlDisplayInfo));
                    bool usable = (display.InfoValue & (DisplayConnected | DisplayMapped)) == (DisplayConnected | DisplayMapped);
                    if (!usable || display.DisplayId.LogicalAdapterIndex != adapter.AdapterIndex) continue;

                    var target = new Target { Adapter = adapter.AdapterIndex, Display = display.DisplayId.LogicalIndex };
                    _targets[adapter.DisplayName] = target;
                    _displays.Add(adapter.DisplayName);
                    _adapterName = adapter.AdapterName.Trim();
                    break;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(info);
            }
        }

        bool TryGetColor(string display, out Target target, out int current, out int def, out int min, out int max)
        {
            current = def = min = max = 0;
            if (!_targets.TryGetValue(display, out target)) return false;
            int step;
            return ADL_Display_Color_Get(target.Adapter, target.Display, AdlDisplayColorSaturation,
                out current, out def, out min, out max, out step) == AdlOk;
        }

        public int GetPercent(string display)
        {
            Target t;
            int cur, def, min, max;
            if (!TryGetColor(display, out t, out cur, out def, out min, out max)) return -1;
            return LevelMap.ToPercent(cur, min, def, max);
        }

        public bool SetPercent(string display, int percent)
        {
            Target t;
            int cur, def, min, max;
            if (!TryGetColor(display, out t, out cur, out def, out min, out max)) return false;
            int value = LevelMap.ToRaw(percent, min, def, max);
            if (value == cur) return true;
            return ADL_Display_Color_Set(t.Adapter, t.Display, AdlDisplayColorSaturation, value) == AdlOk;
        }

        public void SaveOriginal()
        {
            _original.Clear();
            foreach (string display in _displays)
            {
                Target t;
                int cur, def, min, max;
                if (TryGetColor(display, out t, out cur, out def, out min, out max)) _original[display] = cur;
            }
        }

        public void RestoreOriginal()
        {
            foreach (var pair in _original)
            {
                Target t;
                if (_targets.TryGetValue(pair.Key, out t))
                    ADL_Display_Color_Set(t.Adapter, t.Display, AdlDisplayColorSaturation, pair.Value);
            }
        }

        public int LegacyRawToPercent(int raw)
        {
            // vibranceGUI guarda a saturação ADL bruta (padrão 100, máximo 200 na maioria das placas).
            int min = 0, def = 100, max = 200;
            if (_displays.Count > 0)
            {
                Target t;
                int cur;
                TryGetColor(_displays[0], out t, out cur, out def, out min, out max);
            }
            return LevelMap.ToPercent(raw, min, def, max);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ADL_Main_Control_Destroy();
        }
    }
}
