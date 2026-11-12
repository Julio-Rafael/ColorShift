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
        public string ClassName;

        /// <summary>
        /// Janela passageira que não deve mudar o estado: seletor do Alt+Tab, Task View,
        /// ou overlays transparentes/sem ativação (Discord, Opera GX, gravadores etc.).
        /// </summary>
        public bool Transient;

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
        const int GwlExStyle = -20;
        const long WsExTransparent = 0x20, WsExNoActivate = 0x8000000, WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExLayered = 0x80000;
        const uint GaRoot = 2;

        static readonly string[] ShellSwitcherClasses =
        {
            "ForegroundStaging", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
            "TaskSwitcherWnd", "TaskSwitcherOverlayWnd", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
        };

        delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        readonly WinEventProc _proc;
        IntPtr _hook;
        IntPtr _lastHwnd;

        /// <summary>
        /// Rede de segurança: o evento de foreground às vezes não chega (jogo abrindo enquanto outro
        /// programa tem o foco, overlays do Discord/Opera, troca de modo de tela cheia). Confere a cada
        /// 250 ms e avisa em <see cref="Tick"/> para o motor reaplicar o nível se o driver o resetou.
        /// </summary>
        readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 250 };

        public event Action Tick;

        public event Action<ForegroundInfo> Changed;

        public ForegroundWatcher()
        {
            _proc = OnWinEvent;
            _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _proc, 0, 0, WineventOutOfContext);
            if (_hook == IntPtr.Zero)
                throw new InvalidOperationException("SetWinEventHook falhou.");
            _poll.Tick += (s, e) => Poll();
            _poll.Start();
        }

        void Poll()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && hwnd != _lastHwnd) Raise(hwnd);
            var tick = Tick;
            if (tick != null) tick();
        }

        void Raise(IntPtr hwnd)
        {
            _lastHwnd = hwnd;
            var handler = Changed;
            if (handler != null) handler(Describe(hwnd));
        }

        public ForegroundInfo Current()
        {
            return Describe(GetForegroundWindow());
        }

        void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            // O hwnd do evento nem sempre é a janela que ficou de fato em primeiro plano.
            IntPtr actual = GetForegroundWindow();
            if (actual == IntPtr.Zero) actual = hwnd;
            if (actual != IntPtr.Zero) Raise(actual);
        }

        static ForegroundInfo Describe(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                IntPtr root = GetAncestor(hwnd, GaRoot);
                if (root != IntPtr.Zero) hwnd = root;
            }
            var info = new ForegroundInfo { Hwnd = hwnd };
            if (hwnd == IntPtr.Zero) return info;
            GetWindowThreadProcessId(hwnd, out info.ProcessId);
            info.ExePath = GetProcessPath(info.ProcessId);

            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            info.ClassName = cls.ToString();
            long ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            bool overlay = (ex & WsExTransparent) != 0 || (ex & WsExNoActivate) != 0
                || ((ex & WsExToolWindow) != 0 && (ex & WsExTopmost) != 0 && (ex & WsExLayered) != 0);
            info.Transient = overlay || Array.IndexOf(ShellSwitcherClasses, info.ClassName) >= 0;
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
            _poll.Stop();
            _poll.Dispose();
            if (_hook == IntPtr.Zero) return;
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
