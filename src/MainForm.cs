using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ColorShift
{
    sealed class MainForm : Form
    {
        readonly IColorController _controller;
        readonly AppSettings _settings;
        readonly VibranceEngine _engine;
        readonly ForegroundWatcher _watcher;

        readonly TrackBar _desktopTrack = new TrackBar();
        readonly Label _desktopValue = new Label();
        readonly ListView _profiles = new ListView();
        readonly TrackBar _profileTrack = new TrackBar();
        readonly Label _profileValue = new Label();
        readonly Label _profileCaption = new Label();
        readonly CheckBox _autostart = new CheckBox();
        readonly CheckBox _allMonitors = new CheckBox();
        readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        readonly NotifyIcon _tray = new NotifyIcon();
        readonly Timer _saveTimer = new Timer { Interval = 600 };
        readonly CrosshairOverlay _overlay = new CrosshairOverlay();
        readonly ToolStripMenuItem _crosshairMenu = new ToolStripMenuItem("Mira");
        readonly ComboBox _theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly StatusStrip _statusStrip = new StatusStrip();
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        MonitorPage _monitorPage;
        DisplayPage _displayPage;
        CrosshairPage _crosshairPage;
        RamPage _ramPage;
        ThemedTabControl _tabs;
        TabPage _ramTab;

        bool _allowShow;
        bool _exiting;
        bool _trayHintShown;
        bool _loading;

        public MainForm(IColorController controller, bool startMinimized, bool openRamTab = false)
        {
            _controller = controller;
            _settings = AppSettings.Load();
            _engine = new VibranceEngine(controller, _settings);
            _allowShow = !startMinimized;

            _controller.SaveOriginal();
            BuildUi();
            LoadValues();

            _watcher = new ForegroundWatcher();
            _watcher.Changed += _engine.OnForeground;
            _engine.StateChanged += (s, e) => { UpdateStatus(); UpdateCrosshair(); };
            _engine.OnForeground(_watcher.Current());

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); _settings.Save(); };
            Autostart.RefreshPath();
            _ramPage.ApplyOnStartup();
            if (openRamTab)
            {
                _tabs.SelectedTab = _ramTab;
                _ramPage.DetectNow();
            }
        }

        void BuildUi()
        {
            Text = "ColorShift";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(580, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = CreateAppIcon();

            var gpu = new Label
            {
                Text = _controller.Description + "  •  " + _controller.Displays.Count + " monitor(es)",
                Font = new Font("Segoe UI Semibold", 10f),
                Location = new Point(12, 10),
                AutoSize = true
            };

            var desktopGroup = new GroupBox { Text = "Área de trabalho (fora dos jogos)", Location = new Point(12, 40), Size = new Size(536, 78) };
            ConfigureTrack(_desktopTrack, new Point(10, 24), 440);
            _desktopValue.Location = new Point(456, 30);
            _desktopValue.AutoSize = true;
            _desktopTrack.ValueChanged += OnDesktopChanged;
            desktopGroup.Controls.Add(_desktopTrack);
            desktopGroup.Controls.Add(_desktopValue);

            var profileGroup = new GroupBox { Text = "Perfis de jogos", Location = new Point(12, 126), Size = new Size(536, 330) };
            _profiles.Location = new Point(10, 22);
            _profiles.Size = new Size(380, 210);
            _profiles.View = View.Details;
            _profiles.FullRowSelect = true;
            _profiles.HideSelection = false;
            _profiles.MultiSelect = false;
            _profiles.Columns.Add("Jogo", 130);
            _profiles.Columns.Add("Nível", 55);
            _profiles.Columns.Add("Executável", 190);
            _profiles.SelectedIndexChanged += (s, e) => OnProfileSelected();

            var addExe = MakeButton("Adicionar .exe…", 22, AddFromFile);
            var addRunning = MakeButton("Processos abertos…", 56, AddFromRunning);
            var remove = MakeButton("Remover", 90, RemoveSelected);
            var import = MakeButton("Importar do vibranceGUI", 198, ImportVibranceGui);
            import.Height = 34;

            _profileCaption.Location = new Point(10, 246);
            _profileCaption.AutoSize = true;
            ConfigureTrack(_profileTrack, new Point(10, 268), 440);
            _profileValue.Location = new Point(456, 274);
            _profileValue.AutoSize = true;
            _profileTrack.ValueChanged += OnProfileLevelChanged;

            profileGroup.Controls.AddRange(new Control[] { _profiles, addExe, addRunning, remove, import, _profileCaption, _profileTrack, _profileValue });
            foreach (Control c in new Control[] { addExe, addRunning, remove, import }) c.Left = 400;

            _autostart.Text = "Iniciar com o Windows (minimizado na bandeja)";
            _autostart.Location = new Point(14, 466);
            _autostart.AutoSize = true;
            _autostart.CheckedChanged += (s, e) => { if (!_loading) SetAutostart(_autostart.Checked); };

            _allMonitors.Text = "Aplicar o perfil do jogo em todos os monitores (não só no monitor do jogo)";
            _allMonitors.Location = new Point(14, 490);
            _allMonitors.AutoSize = true;
            _allMonitors.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _settings.AllMonitors = _allMonitors.Checked;
                Changed();
            };

            var themeCaption = new Label { Text = "Tema", Location = new Point(378, 14), AutoSize = true };
            _theme.Location = new Point(418, 10);
            _theme.Width = 130;
            _theme.Items.AddRange(Theme.Modes);
            _theme.SelectedIndexChanged += (s, e) =>
            {
                if (_loading) return;
                _settings.ThemeMode = (string)_theme.SelectedItem;
                ApplyTheme();
                _saveTimer.Stop();
                _saveTimer.Start();
            };

            _statusStrip.Items.Add(_status);
            _statusStrip.SizingGrip = false;

            var saturationTab = new TabPage("Saturação");
            saturationTab.Controls.AddRange(new Control[] { gpu, themeCaption, _theme, desktopGroup, profileGroup, _autostart, _allMonitors });

            _monitorPage = new MonitorPage { Location = new Point(12, 10) };
            var monitorTab = new TabPage("Monitor");
            monitorTab.Controls.Add(_monitorPage);

            _crosshairPage = new CrosshairPage(_settings.Crosshair, CrosshairChanged) { Location = new Point(12, 10) };
            var crosshairTab = new TabPage("Mira");
            crosshairTab.Controls.Add(_crosshairPage);

            _displayPage = new DisplayPage { Location = new Point(12, 10) };
            var displayTab = new TabPage("Resolução e FPS");
            displayTab.Controls.Add(_displayPage);

            _ramPage = new RamPage(_settings.RamLeds, () => { _saveTimer.Stop(); _saveTimer.Start(); }, RestartElevated) { Location = new Point(12, 10) };
            var ramTab = _ramTab = new TabPage("Memória RAM");
            ramTab.Controls.Add(_ramPage);

            var tabs = _tabs = new ThemedTabControl { Location = new Point(6, 6), Size = new Size(568, 566) };
            tabs.TabPages.AddRange(new[] { saturationTab, monitorTab, crosshairTab, displayTab, ramTab });

            Controls.AddRange(new Control[] { tabs, _statusStrip });

            var menu = _menu;
            menu.Items.Add("Abrir ColorShift", null, (s, e) => ShowWindow());
            _crosshairMenu.Click += (s, e) => ToggleCrosshair();
            menu.Items.Add(_crosshairMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitApplication());
            _tray.Icon = Icon;
            _tray.Text = "ColorShift";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => ShowWindow();
            ApplyTheme();
        }

        void ApplyTheme()
        {
            Theme.SetMode(_settings.ThemeMode);
            Theme.Apply(this);
            Theme.Apply(_menu);
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // UserPreferenceChanged dispara muito (qualquer ajuste do sistema); repintar a janela toda só se o tema mudou.
            if (e.Category != UserPreferenceCategory.General || _settings.ThemeMode != "Sistema") return;
            bool wasDark = Theme.Dark;
            Theme.SetMode(_settings.ThemeMode);
            if (Theme.Dark != wasDark) ApplyTheme();
        }

        void ConfigureTrack(TrackBar track, Point location, int width)
        {
            track.Location = location;
            track.Width = width;
            track.Minimum = _controller.MinPercent;
            track.Maximum = 100;
            track.TickFrequency = 5;
            track.SmallChange = 1;
            track.LargeChange = 5;
        }

        Button MakeButton(string text, int top, Action onClick)
        {
            var b = new Button { Text = text, Top = top, Width = 126, Height = 28 };
            b.Click += (s, e) => onClick();
            return b;
        }

        void LoadValues()
        {
            _loading = true;
            _desktopTrack.Value = Clamp(_settings.DesktopLevel);
            _desktopValue.Text = _desktopTrack.Value + "%";
            _autostart.Checked = Autostart.Enabled;
            _allMonitors.Checked = _settings.AllMonitors;
            _theme.SelectedItem = Array.IndexOf(Theme.Modes, _settings.ThemeMode) >= 0 ? _settings.ThemeMode : Theme.Modes[0];
            RefreshProfileList(null);
            _loading = false;
        }

        int Clamp(int percent)
        {
            return Math.Max(_controller.MinPercent, Math.Min(100, percent));
        }

        void RefreshProfileList(Profile select)
        {
            _profiles.BeginUpdate();
            _profiles.Items.Clear();
            foreach (var p in _settings.Profiles)
            {
                var item = new ListViewItem(new[] { p.Name, p.Level + "%", p.ExePath }) { Tag = p };
                _profiles.Items.Add(item);
                if (p == select) item.Selected = true;
            }
            _profiles.EndUpdate();
            OnProfileSelected();
        }

        Profile SelectedProfile
        {
            get { return _profiles.SelectedItems.Count == 0 ? null : (Profile)_profiles.SelectedItems[0].Tag; }
        }

        void OnProfileSelected()
        {
            Profile p = SelectedProfile;
            _profileTrack.Enabled = p != null;
            bool wasLoading = _loading;
            _loading = true;
            _profileTrack.Value = Clamp(p == null ? 50 : p.Level);
            _loading = wasLoading;
            _profileCaption.Text = p == null ? "Selecione um perfil para ajustar o nível." : "Nível de vibrance de \"" + p.Name + "\":";
            _profileValue.Text = p == null ? "" : _profileTrack.Value + "%";
        }

        void OnDesktopChanged(object sender, EventArgs e)
        {
            _desktopValue.Text = _desktopTrack.Value + "%";
            if (_loading) return;
            _settings.DesktopLevel = _desktopTrack.Value;
            Changed();
        }

        void OnProfileLevelChanged(object sender, EventArgs e)
        {
            Profile p = SelectedProfile;
            if (_loading || p == null) return;
            p.Level = _profileTrack.Value;
            _profileValue.Text = p.Level + "%";
            _profiles.SelectedItems[0].SubItems[1].Text = p.Level + "%";
            Changed();
        }

        void Changed()
        {
            _engine.Apply();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void AddProfile(string exePath)
        {
            Profile existing = _settings.Profiles.Find(p => string.Equals(p.ExePath, exePath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new Profile { Name = Path.GetFileNameWithoutExtension(exePath), ExePath = exePath, Level = 75 };
                _settings.Profiles.Add(existing);
                Changed();
            }
            RefreshProfileList(existing);
        }

        void AddFromFile()
        {
            using (var dialog = new OpenFileDialog { Filter = "Executáveis (*.exe)|*.exe", Title = "Escolha o executável do jogo" })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddProfile(dialog.FileName);
        }

        void AddFromRunning()
        {
            using (var picker = new ProcessPicker())
                if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedPath != null) AddProfile(picker.SelectedPath);
        }

        void RemoveSelected()
        {
            Profile p = SelectedProfile;
            if (p == null) return;
            _settings.Profiles.Remove(p);
            Changed();
            RefreshProfileList(null);
        }

        void ImportVibranceGui()
        {
            try
            {
                int added = _settings.ImportVibranceGui(_controller);
                LoadValues();
                Changed();
                MessageBox.Show(this, added + " perfil(is) importado(s) do vibranceGUI.", "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void SetAutostart(bool enabled)
        {
            try
            {
                Autostart.Enabled = enabled;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Não foi possível alterar a inicialização automática: " + ex.Message, "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void CrosshairChanged()
        {
            UpdateCrosshair();
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void ToggleCrosshair()
        {
            _settings.Crosshair.Enabled = !_settings.Crosshair.Enabled;
            _crosshairPage.Reload();
            CrosshairChanged();
        }

        /// <summary>Mostra a mira no monitor do jogo ativo (ou no principal) conforme as opções.</summary>
        void UpdateCrosshair()
        {
            CrosshairSettings c = _settings.Crosshair;
            IntPtr game = _engine.ActiveWindow;
            bool visible = c.Enabled && (!c.OnlyInGames || game != IntPtr.Zero);
            Screen screen = game != IntPtr.Zero ? Screen.FromHandle(game) : Screen.PrimaryScreen;
            _overlay.Place(c, screen, visible);
            _crosshairMenu.Checked = c.Enabled;
        }

        void UpdateStatus()
        {
            Profile p = _engine.ActiveProfile;
            string text = p == null
                ? "Área de trabalho: " + _settings.DesktopLevel + "%"
                : "Jogo ativo: " + p.Name + " (" + p.Level + "%)";
            _status.Text = text;
            _tray.Text = "ColorShift — " + (text.Length > 50 ? text.Substring(0, 50) : text);
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _controller.Refresh();
            _engine.Apply();
            _monitorPage.OnDisplaySettingsChanged();
            _displayPage.OnDisplaySettingsChanged();
        }

        void ShowWindow()
        {
            _allowShow = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(3000, "ColorShift", "Continuo rodando na bandeja. Clique com o botão direito no ícone para sair.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>Abre uma cópia do ColorShift como administrador (pede o UAC) e fecha esta.</summary>
        void RestartElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--elevated-restart --ram")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Win32Exception)
            {
                return; // o usuário recusou o UAC
            }
            ExitApplication();
        }

        void ExitApplication()
        {
            _exiting = true;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _watcher.Dispose();
            _overlay.Dispose();
            _monitorPage.Dispose();
            _ramPage.Dispose();
            _saveTimer.Stop();
            _settings.Save();
            _controller.RestoreOriginal();
            _controller.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            base.OnFormClosed(e);
        }

        static Icon CreateAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var rect = new Rectangle(2, 2, 28, 28);
                    using (var brush = new LinearGradientBrush(rect, Color.FromArgb(255, 64, 129), Color.FromArgb(0, 200, 255), 45f))
                        g.FillEllipse(brush, rect);
                    using (var pen = new Pen(Color.White, 2.5f))
                        g.DrawArc(pen, 9, 9, 14, 14, 200, 280);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
