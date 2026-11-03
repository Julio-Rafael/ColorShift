using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ColorShift
{
    sealed class ForegroundInfo
    {
        public IntPtr Hwnd;
        public uint ProcessId;
        public string ExePath;

        public string ExeName
        {
            get { return string.IsNullOrEmpty(ExePath) ? "" : Path.GetFileNameWithoutExtension(ExePath); }
        }
    }

    /// <summary>
    /// Observa a troca de janela em primeiro plano (EVENT_SYSTEM_FOREGROUND), como o WinEventHook do vibranceGUI.
    /// O callback chega na thread da UI porque o hook é "out of context" e a thread tem loop de mensagens.
    /// </summary>
    sealed class ForegroundWatcher : IDisposable
    {
        const uint EventSystemForeground = 0x0003;
        const uint WineventOutOfContext = 0x0000;
        const uint ProcessQueryLimitedInformation = 0x1000;

        delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        readonly WinEventProc _proc;
        IntPtr _hook;

        public event Action<ForegroundInfo> Changed;

        public ForegroundWatcher()
        {
            _proc = OnWinEvent;
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WineventOutOfContext);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException("SetWinEventHook falhou.");
        }

        public ForegroundInfo Current()
        {
            return Describe(GetForegroundWindow());
        }

        void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            var handler = Changed;
            if (handler != null && hwnd != IntPtr.Zero) handler(Describe(hwnd));
        }

        static ForegroundInfo Describe(IntPtr hwnd)
        {
            var info = new ForegroundInfo { Hwnd = hwnd };
            if (hwnd == IntPtr.Zero) return info;
            GetWindowThreadProcessId(hwnd, out info.ProcessId);
            info.ExePath = GetProcessPath(info.ProcessId);
            return info;
        }

        /// <summary>
        /// Usa PROCESS_QUERY_LIMITED_INFORMATION, que funciona até com processos protegidos/elevados
        /// (Process.MainModule falharia, por exemplo, em jogos com anti-cheat).
        /// </summary>
        public static string GetProcessPath(uint processId)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
