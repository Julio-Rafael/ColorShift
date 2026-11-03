using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>
    /// Controle de Digital Vibrance pela NvAPI. Os IDs de interface abaixo são os mesmos
    /// encontrados no vibranceDLL.dll do vibranceGUI (ver docs/ANALISE.md).
    /// </summary>
    sealed class NvidiaController : IColorController
    {
        const uint IdInitialize = 0x0150E828;
        const uint IdUnload = 0xD22BDD7E;
        const uint IdEnumPhysicalGPUs = 0xE5AC921F;
        const uint IdGpuGetFullName = 0xCEEE8E9F;
        const uint IdGetAssociatedNvidiaDisplayHandle = 0x35C29134;
        const uint IdGetDVCInfo = 0x4085DE45;
        const uint IdSetDVCLevel = 0x172409B4;

        const int NvapiOk = 0;
        const int MaxPhysicalGpus = 64;

        [StructLayout(LayoutKind.Sequential)]
        struct NvDisplayDvcInfo
        {
            public uint Version;
            public int CurrentLevel;
            public int MinLevel;
            public int MaxLevel;
        }

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int UnloadFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumPhysicalGPUsFn([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GpuGetFullNameFn(IntPtr gpu, [Out] byte[] name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetAssociatedHandleFn([MarshalAs(UnmanagedType.LPStr)] string displayName, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetDVCInfoFn(IntPtr display, uint outputId, ref NvDisplayDvcInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetDVCLevelFn(IntPtr display, uint outputId, int level);

        readonly UnloadFn _unload;
        readonly GetAssociatedHandleFn _getAssociatedHandle;
        readonly GetDVCInfoFn _getDvcInfo;
        readonly SetDVCLevelFn _setDvcLevel;

        readonly List<string> _displays = new List<string>();
        readonly Dictionary<string, IntPtr> _handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _original = new Dictionary<string, int>();
        readonly string _gpuName;
        bool _disposed;

        public NvidiaController()
        {
            var initialize = Get<InitializeFn>(IdInitialize);
            _unload = Get<UnloadFn>(IdUnload);
            _getAssociatedHandle = Get<GetAssociatedHandleFn>(IdGetAssociatedNvidiaDisplayHandle);
            _getDvcInfo = Get<GetDVCInfoFn>(IdGetDVCInfo);
            _setDvcLevel = Get<SetDVCLevelFn>(IdSetDVCLevel);

            int status = initialize();
            if (status != NvapiOk)
                throw new InvalidOperationException("NvAPI_Initialize falhou (status " + status + ").");

            _gpuName = ReadGpuName();
            Refresh();
        }

        static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero)
                throw new InvalidOperationException(string.Format("Função NvAPI 0x{0:X8} indisponível neste driver.", id));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        string ReadGpuName()
        {
            try
            {
                var enumGpus = Get<EnumPhysicalGPUsFn>(IdEnumPhysicalGPUs);
                var getName = Get<GpuGetFullNameFn>(IdGpuGetFullName);
                var gpus = new IntPtr[MaxPhysicalGpus];
                int count;
                if (enumGpus(gpus, out count) != NvapiOk || count < 1) return "NVIDIA";
                var buffer = new byte[64];
                if (getName(gpus[0], buffer) != NvapiOk) return "NVIDIA";
                string name = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
                return name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase) ? name : "NVIDIA " + name;
            }
            catch (InvalidOperationException)
            {
                return "NVIDIA";
            }
        }

        public string Description { get { return _gpuName; } }
        public int MinPercent { get { return 50; } }
        public IList<string> Displays { get { return _displays.AsReadOnly(); } }

        public void Refresh()
        {
            _displays.Clear();
            _handles.Clear();
            foreach (Screen screen in Screen.AllScreens)
            {
                IntPtr handle;
                if (_getAssociatedHandle(screen.DeviceName, out handle) == NvapiOk)
                {
                    _displays.Add(screen.DeviceName);
                    _handles[screen.DeviceName] = handle;
                }
            }
        }

        static readonly uint InfoVersion = (uint)Marshal.SizeOf(typeof(NvDisplayDvcInfo)) | (1u << 16);

        bool TryGetInfo(string display, out IntPtr handle, out NvDisplayDvcInfo info)
        {
            info = new NvDisplayDvcInfo();
            info.Version = InfoVersion;
            // O handle fica em cache; se deixar de valer (monitor trocou), pede de novo uma vez.
            if (_handles.TryGetValue(display, out handle) && _getDvcInfo(handle, 0, ref info) == NvapiOk) return true;
            if (_getAssociatedHandle(display, out handle) != NvapiOk) return false;
            info = new NvDisplayDvcInfo { Version = InfoVersion };
            if (_getDvcInfo(handle, 0, ref info) != NvapiOk) return false;
            _handles[display] = handle;
            return true;
        }

        public int GetPercent(string display)
        {
            IntPtr handle;
            NvDisplayDvcInfo info;
            if (!TryGetInfo(display, out handle, out info)) return -1;
            return LevelMap.ToPercent(info.CurrentLevel, info.MinLevel, info.MinLevel, info.MaxLevel);
        }

        public bool SetPercent(string display, int percent)
        {
            IntPtr handle;
            NvDisplayDvcInfo info;
            if (!TryGetInfo(display, out handle, out info)) return false;
            int level = LevelMap.ToRaw(percent, info.MinLevel, info.MinLevel, info.MaxLevel);
            return SetRaw(handle, info, level);
        }

        bool SetRaw(IntPtr handle, NvDisplayDvcInfo info, int level)
        {
            level = Math.Max(info.MinLevel, Math.Min(info.MaxLevel, level));
            if (info.CurrentLevel == level) return true;
            return _setDvcLevel(handle, 0, level) == NvapiOk;
        }

        public void SaveOriginal()
        {
            _original.Clear();
            foreach (string display in _displays)
            {
                IntPtr handle;
                NvDisplayDvcInfo info;
                if (TryGetInfo(display, out handle, out info)) _original[display] = info.CurrentLevel;
            }
        }

        public void RestoreOriginal()
        {
            foreach (var pair in _original)
            {
                IntPtr handle;
                NvDisplayDvcInfo info;
                if (TryGetInfo(pair.Key, out handle, out info)) SetRaw(handle, info, pair.Value);
            }
        }

        public int LegacyRawToPercent(int raw)
        {
            // vibranceGUI guarda o nível DVC bruto (0..63 nas placas atuais).
            int max = 63;
            IntPtr handle;
            NvDisplayDvcInfo info;
            if (_displays.Count > 0 && TryGetInfo(_displays[0], out handle, out info)) max = info.MaxLevel;
            return LevelMap.ToPercent(raw, 0, 0, max);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _unload();
        }
    }
}
