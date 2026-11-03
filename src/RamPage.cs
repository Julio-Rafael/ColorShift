using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorShift
{
    public class RamLedSettings
    {
        public bool ApplyOnStart;
        public string Effect = "Static";
        public int ColorRgb = 0x00A0FF;
        public int Speed = 50;
        public int Brightness = 100;
    }

    /// <summary>
    /// Aba "Memória RAM": muda os LEDs dos pentes direto pelo SMBus (driver PawnIO), sem o OpenRGB.
    /// O acesso ao hardware roda fora da thread da interface, em uma fila, para os comandos chegarem na ordem.
    /// </summary>
    sealed class RamPage : UserControl
    {
        const string AllModules = "Todos os pentes";

        readonly RamLedSettings _settings;
        readonly Action _changed;
        readonly Action _restartElevated;
        readonly Label _status = new Label();
        readonly Button _detect;
        readonly Button _elevate;
        readonly ComboBox _modules = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox _effects = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Panel _swatch = new Panel { BorderStyle = BorderStyle.FixedSingle, Tag = Theme.KeepTag };
        readonly Button _pickColor;
        readonly TrackBar _speed = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Height = 30 };
        readonly TrackBar _brightness = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Height = 30 };
        readonly Label _speedValue = new Label { AutoSize = true };
        readonly Label _brightnessValue = new Label { AutoSize = true };
        readonly Button _off;
        readonly Button _save;
        readonly CheckBox _applyOnStart = new CheckBox { Text = "Reaplicar estes LEDs quando o ColorShift abrir como administrador", AutoSize = true };
        readonly GroupBox _controls;
        readonly Timer _applyTimer = new Timer { Interval = 120 };

        // Só a fila de trabalho mexe nestes dois.
        List<PawnIoSmbus> _buses = new List<PawnIoSmbus>();
        List<EneDramDevice> _hardware = new List<EneDramDevice>();

        List<EneDramDevice> _devices = new List<EneDramDevice>();
        Task _queue = Task.FromResult(0);
        bool _loaded;
        bool _loading;

        public RamPage(RamLedSettings settings, Action changed, Action restartElevated)
        {
            _settings = settings;
            _changed = changed;
            _restartElevated = restartElevated;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(536, 500);

            var server = new GroupBox { Text = "Acesso à memória", Location = new Point(0, 0), Size = new Size(536, 104) };
            _status.Location = new Point(10, 22);
            _status.Size = new Size(516, 36);
            _status.Tag = Theme.MutedTag;
            _detect = new Button { Text = "Detectar pentes", Location = new Point(10, 64), Size = new Size(130, 28) };
            _detect.Click += (s, e) => Detect(false);
            _elevate = new Button { Text = "Reiniciar como administrador", Location = new Point(148, 64), Size = new Size(200, 28) };
            _elevate.Click += (s, e) => _restartElevated();
            server.Controls.AddRange(new Control[] { _status, _detect, _elevate });

            _controls = new GroupBox { Text = "LEDs da memória", Location = new Point(0, 112), Size = new Size(536, 262) };
            AddCaption("Pente", 27);
            _modules.Location = new Point(104, 23);
            _modules.Width = 420;
            _modules.SelectedIndexChanged += (s, e) => { if (!_loading) OnModuleChanged(); };

            AddCaption("Efeito", 63);
            _effects.Location = new Point(104, 59);
            _effects.Width = 220;
            _effects.SelectedIndexChanged += (s, e) => { if (!_loading) OnEffectChanged(); };

            AddCaption("Cor", 99);
            _swatch.Location = new Point(104, 95);
            _swatch.Size = new Size(50, 26);
            _swatch.Cursor = Cursors.Hand;
            _swatch.Click += (s, e) => PickColor();
            _pickColor = new Button { Text = "Escolher cor…", Location = new Point(162, 94), Size = new Size(110, 28) };
            _pickColor.Click += (s, e) => PickColor();

            AddCaption("Velocidade", 139);
            AddTrack(_speed, _speedValue, 133);
            AddCaption("Brilho", 179);
            AddTrack(_brightness, _brightnessValue, 173);
            _speed.ValueChanged += (s, e) => OnTrackChanged(_speed, _speedValue);
            _brightness.ValueChanged += (s, e) => OnTrackChanged(_brightness, _brightnessValue);

            _off = new Button { Text = "Desligar LEDs", Location = new Point(10, 220), Size = new Size(130, 30) };
            _off.Click += (s, e) => TurnOff();
            _save = new Button { Text = "Gravar no pente…", Location = new Point(148, 220), Size = new Size(140, 30) };
            _save.Click += (s, e) => SaveToDevice();
            _controls.Controls.AddRange(new Control[] { _modules, _effects, _swatch, _pickColor, _speed, _speedValue, _brightness, _brightnessValue, _off, _save });

            _applyOnStart.Location = new Point(2, 382);
            _applyOnStart.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _settings.ApplyOnStart = _applyOnStart.Checked;
                _changed();
            };

            Controls.AddRange(new Control[] { server, _controls, _applyOnStart });

            _applyTimer.Tick += (s, e) => { _applyTimer.Stop(); ApplyCurrent(); };

            _loading = true;
            _speed.Value = Math.Max(0, Math.Min(100, _settings.Speed));
            _brightness.Value = Math.Max(0, Math.Min(100, _settings.Brightness));
            _speedValue.Text = _speed.Value + "%";
            _brightnessValue.Text = _brightness.Value + "%";
            _applyOnStart.Checked = _settings.ApplyOnStart;
            _swatch.BackColor = ColorOf(_settings.ColorRgb);
            _loading = false;

            _elevate.Visible = !IsElevated;
            SetStatus(IsElevated
                ? "Clique em Detectar pentes para procurar os pentes de memória com LED."
                : "Para falar com a memória o Windows exige administrador. Clique em Reiniciar como administrador.");
            UpdateEnabled();
        }

        static readonly bool Elevated = CheckElevated();

        public static bool IsElevated
        {
            get { return Elevated; }
        }

        static bool CheckElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static Color ColorOf(int rgb)
        {
            return Color.FromArgb(255, Color.FromArgb(rgb & 0xFFFFFF));
        }

        void AddCaption(string text, int top)
        {
            _controls.Controls.Add(new Label { Text = text, Location = new Point(10, top), AutoSize = true });
        }

        void AddTrack(TrackBar track, Label value, int top)
        {
            track.Location = new Point(104, top);
            track.Width = 360;
            track.LargeChange = 10;
            value.Location = new Point(470, top + 6);
        }

        /// <summary>Ao abrir o app como administrador, detecta os pentes e reaplica o efeito salvo.</summary>
        public void ApplyOnStartup()
        {
            if (!_settings.ApplyOnStart || !IsElevated) return;
            Detect(true);
        }

        /// <summary>Abre direto na aba quando o app foi reiniciado como administrador a partir dela.</summary>
        public void DetectNow()
        {
            if (!_loaded) Detect(false);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && !_loaded && !DesignMode && IsElevated) Detect(false);
        }

        void SetStatus(string text)
        {
            _status.Text = text;
        }

        void UpdateEnabled()
        {
            EneMode mode = SelectedModes().Select(p => p.Value).FirstOrDefault();
            bool any = _devices.Count > 0;
            _modules.Enabled = any;
            _effects.Enabled = any;
            _off.Enabled = any;
            _save.Enabled = any;
            bool color = mode != null && mode.HasColor;
            _swatch.Enabled = color;
            _pickColor.Enabled = color;
            _brightness.Enabled = color; // o ENE não tem brilho: ele é aplicado escurecendo a cor
            _speed.Enabled = mode != null && mode.HasSpeed;
        }

        /// <summary>Executa em segundo plano, um trabalho por vez.</summary>
        void Enqueue(Action work, Action done, Action<Exception> failed)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                Exception error = null;
                try { work(); }
                catch (Exception ex) { error = ex; }
                if (!IsHandleCreated || IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) return;
                        if (error == null) done();
                        else failed(error);
                    }));
                }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
        }

        static bool OpenRgbRunning()
        {
            Process[] running = Process.GetProcessesByName("OpenRGB");
            foreach (var p in running) p.Dispose();
            return running.Length > 0;
        }

        void Detect(bool applyAfter)
        {
            if (!IsHandleCreated) CreateHandle();
            _loaded = true;
            if (!IsElevated)
            {
                SetStatus("Para falar com a memória o Windows exige administrador. Clique em Reiniciar como administrador.");
                return;
            }
            if (OpenRgbRunning())
            {
                SetStatus("Feche o OpenRGB antes: dois programas mexendo nos LEDs ao mesmo tempo podem se atrapalhar.");
                return;
            }

            _detect.Enabled = false;
            SetStatus("Procurando os pentes de memória…");
            List<EneDramDevice> found = null;
            Enqueue(() =>
                {
                    CloseHardware();
                    _buses = PawnIoSmbus.OpenDramBuses();
                    if (_buses.Count == 0)
                        throw new PawnIoException("Nenhum SMBus de memória compatível foi encontrado (Intel i801 ou AMD PIIX4). " +
                            "Confira se SmbusI801.bin / SmbusPIIX4.bin estão na pasta do ColorShift.");
                    _hardware = _buses.SelectMany(EneDramDevice.Detect).ToList();
                    found = _hardware.ToList();
                },
                () =>
                {
                    _detect.Enabled = true;
                    ShowDevices(found);
                    if (applyAfter && _devices.Count > 0) ApplyCurrent();
                },
                error =>
                {
                    _detect.Enabled = true;
                    ShowDevices(new List<EneDramDevice>());
                    SetStatus(error is PawnIoException || error is DllNotFoundException
                        ? error.Message
                        : "Erro ao acessar a memória: " + error.Message);
                });
        }

        void CloseHardware()
        {
            foreach (var bus in _buses) bus.Dispose();
            _buses = new List<PawnIoSmbus>();
            _hardware = new List<EneDramDevice>();
        }

        void ShowDevices(List<EneDramDevice> devices)
        {
            _devices = devices;
            _loading = true;
            _modules.Items.Clear();
            _modules.Items.Add(AllModules);
            foreach (var d in _devices) _modules.Items.Add(d);
            _modules.SelectedIndex = 0;
            _loading = false;
            FillEffects();
            UpdateEnabled();
            if (_devices.Count > 0)
                SetStatus(_devices.Count + " pente(s) de memória com LED detectado(s).");
            else
                SetStatus("Nenhum pente com controlador ENE foi detectado. Este modelo de memória pode não ser suportado.");
        }

        /// <summary>Os pentes selecionados, cada um com o modo do efeito escolhido (quando ele tem esse efeito).</summary>
        List<KeyValuePair<EneDramDevice, EneMode>> SelectedModes()
        {
            var result = new List<KeyValuePair<EneDramDevice, EneMode>>();
            string effect = _effects.SelectedItem as string;
            if (effect == null) return result;
            foreach (var d in TargetDevices())
            {
                EneMode mode = d.Modes.FirstOrDefault(m => m.Name == effect);
                if (mode != null) result.Add(new KeyValuePair<EneDramDevice, EneMode>(d, mode));
            }
            return result;
        }

        List<EneDramDevice> TargetDevices()
        {
            var one = _modules.SelectedItem as EneDramDevice;
            return one != null ? new List<EneDramDevice> { one } : _devices;
        }

        void FillEffects()
        {
            string wanted = _effects.SelectedItem as string ?? _settings.Effect;
            _loading = true;
            _effects.Items.Clear();
            foreach (string name in TargetDevices().SelectMany(d => d.Modes).Select(m => m.Name).Distinct())
                _effects.Items.Add(name);
            if (_effects.Items.Contains(wanted)) _effects.SelectedItem = wanted;
            else if (_effects.Items.Count > 0) _effects.SelectedIndex = 0;
            _loading = false;
        }

        void OnModuleChanged()
        {
            FillEffects();
            UpdateEnabled();
            Remember();
            ApplyCurrent();
        }

        void OnEffectChanged()
        {
            UpdateEnabled();
            Remember();
            ApplyCurrent();
        }

        void OnTrackChanged(TrackBar track, Label value)
        {
            value.Text = track.Value + "%";
            if (_loading) return;
            Remember();
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        void PickColor()
        {
            using (var dialog = new ColorDialog { Color = ColorOf(_settings.ColorRgb), FullOpen = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.ColorRgb = dialog.Color.ToArgb() & 0xFFFFFF;
                _swatch.BackColor = ColorOf(_settings.ColorRgb);
            }
            _changed();
            ApplyCurrent();
        }

        void Remember()
        {
            string effect = _effects.SelectedItem as string;
            if (effect != null) _settings.Effect = effect;
            _settings.Speed = _speed.Value;
            _settings.Brightness = _brightness.Value;
            _changed();
        }

        static int Dim(int rgb, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            int r = ((rgb >> 16) & 0xFF) * percent / 100;
            int g = ((rgb >> 8) & 0xFF) * percent / 100;
            int b = (rgb & 0xFF) * percent / 100;
            return (r << 16) | (g << 8) | b;
        }

        void ApplyCurrent()
        {
            var targets = SelectedModes();
            if (targets.Count == 0) return;
            Send(targets, Dim(_settings.ColorRgb, _brightness.Value), "Aplicado.");
        }

        void TurnOff()
        {
            // Com o efeito "Off" selecionado, a escolha fica salva e não religa ao abrir o app.
            if (_effects.Items.Contains("Off") && (_effects.SelectedItem as string) != "Off")
            {
                _effects.SelectedItem = "Off";
                SetStatus("LEDs desligados.");
                return;
            }
            var targets = TargetDevices()
                .Select(d => new KeyValuePair<EneDramDevice, EneMode>(d, d.Modes.First(m => m.Value == 0)))
                .ToList();
            Send(targets, 0, "LEDs desligados.");
        }

        void Send(List<KeyValuePair<EneDramDevice, EneMode>> targets, int rgb, string message)
        {
            int speed = _speed.Value;
            Enqueue(() =>
                {
                    foreach (var t in targets) t.Key.Apply(t.Value, rgb, speed);
                },
                () => SetStatus(message),
                error => SetStatus("Não foi possível aplicar: " + error.Message));
        }

        /// <summary>Grava o efeito na memória interna do pente. Só quando o usuário pede: é uma escrita em flash.</summary>
        void SaveToDevice()
        {
            var answer = MessageBox.Show(this,
                "Gravar o efeito atual na memória do pente?\n\nAssim ele continua com estes LEDs mesmo sem o ColorShift " +
                "aberto e depois de desligar o PC. Grave só quando terminar de ajustar (cada gravação usa a memória flash do pente).",
                "Gravar no pente", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            var targets = TargetDevices();
            Enqueue(() =>
                {
                    foreach (var d in targets) d.SaveToDevice();
                },
                () => SetStatus("Efeito gravado no pente."),
                error => SetStatus("Não foi possível gravar: " + error.Message));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _applyTimer.Dispose();
                // Espera os comandos pendentes terminarem antes de fechar o driver.
                Enqueue(CloseHardware, () => { }, e => { });
                try { _queue.Wait(3000); } catch (AggregateException) { }
            }
            base.Dispose(disposing);
        }
    }
}
