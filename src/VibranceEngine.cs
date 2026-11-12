using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>
    /// Decide o nível de cada monitor a partir da janela em primeiro plano.
    /// Jogo com perfil em foco: o monitor do jogo (ou todos) recebe o nível do perfil;
    /// o resto fica no nível da área de trabalho.
    /// </summary>
    sealed class VibranceEngine
    {
        readonly IColorController _controller;
        readonly AppSettings _settings;
        readonly uint _ownProcessId = (uint)Process.GetCurrentProcess().Id;
        ForegroundInfo _foreground;

        public Profile ActiveProfile { get; private set; }

        /// <summary>Janela do jogo ativo, ou IntPtr.Zero fora dos jogos.</summary>
        public IntPtr ActiveWindow
        {
            get { return ActiveProfile == null || _foreground == null ? IntPtr.Zero : _foreground.Hwnd; }
        }
        public event EventHandler StateChanged;

        public VibranceEngine(IColorController controller, AppSettings settings)
        {
            _controller = controller;
            _settings = settings;
        }

        public void OnForeground(ForegroundInfo info)
        {
            // Alt+Tab, Task View e overlays (Discord, Opera GX...) não tiram o jogo do estado ativo.
            if (info.Transient && _foreground != null) return;
            _foreground = info;
            Apply();
        }

        /// <summary>
        /// Chamado periodicamente. O driver volta o vibrance ao padrão quando o jogo troca de modo
        /// (Alt+Tab em tela cheia); reaplica na hora em vez de esperar o DisplaySettingsChanged.
        /// Só avisa a interface se o jogo ativo mudou.
        /// </summary>
        public void Enforce()
        {
            Profile before = ActiveProfile;
            IntPtr window = ActiveWindow;
            Apply(false);
            if (ActiveProfile != before || ActiveWindow != window) RaiseStateChanged();
        }

        public void Apply()
        {
            Apply(true);
        }

        void Apply(bool notify)
        {
            Profile profile = null;
            if (_foreground != null && _foreground.ProcessId != _ownProcessId)
                profile = _settings.FindProfile(_foreground);

            string gameDisplay = null;
            if (profile != null && !_settings.AllMonitors)
                gameDisplay = Screen.FromHandle(_foreground.Hwnd).DeviceName;

            foreach (string display in _controller.Displays)
            {
                bool useProfile = profile != null && (_settings.AllMonitors || string.Equals(display, gameDisplay, StringComparison.OrdinalIgnoreCase));
                _controller.SetPercent(display, useProfile ? profile.Level : _settings.DesktopLevel);
            }

            ActiveProfile = profile;
            if (notify) RaiseStateChanged();
        }

        void RaiseStateChanged()
        {
            var handler = StateChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }
}
