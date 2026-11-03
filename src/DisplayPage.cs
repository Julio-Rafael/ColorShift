using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>Resolução e taxa de atualização (Hz) pelo ChangeDisplaySettingsEx, o mesmo caminho do vibranceGUI.</summary>
    static class DisplayModes
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields;
            public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public short LogPixels;
            public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        const int EnumCurrentSettings = -1;
        const int DmBitsPerPel = 0x40000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
        const uint CdsUpdateRegistry = 0x1, CdsTest = 0x2;
        public const int DispChangeSuccessful = 0, DispChangeRestart = 1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int ChangeDisplaySettingsEx(string device, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr param);

        static DevMode NewDevMode()
        {
            return new DevMode { Size = (short)Marshal.SizeOf(typeof(DevMode)) };
        }

        public static DevMode Current(string device)
        {
            DevMode dm = NewDevMode();
            EnumDisplaySettings(device, EnumCurrentSettings, ref dm);
            return dm;
        }

        /// <summary>Modos que o driver oferece, com a mesma profundidade de cor do modo atual.</summary>
        public static List<DevMode> All(string device)
        {
            int bpp = Current(device).BitsPerPel;
            var list = new List<DevMode>();
            DevMode dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.BitsPerPel == bpp) list.Add(dm);
                dm = NewDevMode();
            }
            return list;
        }

        public static int Apply(string device, int width, int height, int hz, int bpp)
        {
            DevMode dm = NewDevMode();
            dm.PelsWidth = width;
            dm.PelsHeight = height;
            dm.DisplayFrequency = hz;
            dm.BitsPerPel = bpp;
            dm.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmBitsPerPel;
            int test = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsTest, IntPtr.Zero);
            if (test != DispChangeSuccessful) return test;
            return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        }
    }

    sealed class DisplayPage : UserControl
    {
        readonly ComboBox _screens = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox _resolution = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox _refresh = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label _current = new Label();
        readonly Button _apply = new Button();
        List<DisplayModes.DevMode> _modes = new List<DisplayModes.DevMode>();
        bool _loading;
        bool _loaded;

        sealed class ScreenItem
        {
            public readonly Screen Screen;
            public ScreenItem(Screen screen) { Screen = screen; }
            public override string ToString()
            {
                return Screen.DeviceName.Replace(@"\\.\", "") + (Screen.Primary ? " (principal)" : "");
            }
        }

        sealed class SizeItem
        {
            public readonly int Width, Height;
            public SizeItem(int w, int h) { Width = w; Height = h; }
            public override string ToString() { return Width + " × " + Height; }
        }

        sealed class HzItem
        {
            public readonly int Hz;
            public HzItem(int hz) { Hz = hz; }
            public override string ToString() { return Hz + " Hz"; }
        }

        string Device
        {
            get { var item = _screens.SelectedItem as ScreenItem; return item == null ? null : item.Screen.DeviceName; }
        }

        public DisplayPage()
        {
            Font = new Font("Segoe UI", 9f);
            Size = new Size(536, 500);

            var group = new GroupBox { Text = "Resolução e FPS (taxa de atualização)", Location = new Point(0, 0), Size = new Size(536, 200) };
            group.Controls.Add(new Label { Text = "Monitor", Location = new Point(10, 29), AutoSize = true });
            _screens.Location = new Point(110, 25);
            _screens.Width = 220;
            _screens.SelectedIndexChanged += (s, e) => LoadModes();
            group.Controls.Add(_screens);

            group.Controls.Add(new Label { Text = "Resolução", Location = new Point(10, 65), AutoSize = true });
            _resolution.Location = new Point(110, 61);
            _resolution.Width = 220;
            _resolution.SelectedIndexChanged += (s, e) => LoadRefreshRates();
            group.Controls.Add(_resolution);

            group.Controls.Add(new Label { Text = "Frequência", Location = new Point(10, 101), AutoSize = true });
            _refresh.Location = new Point(110, 97);
            _refresh.Width = 220;
            group.Controls.Add(_refresh);

            _apply.Text = "Aplicar";
            _apply.Location = new Point(110, 136);
            _apply.Size = new Size(120, 30);
            _apply.Click += (s, e) => ApplySelected();
            group.Controls.Add(_apply);

            var native = new Button { Text = "Máximo do monitor", Location = new Point(240, 136), Size = new Size(140, 30) };
            native.Click += (s, e) => SelectBest();
            group.Controls.Add(native);

            _current.Location = new Point(10, 174);
            _current.AutoSize = true;
            _current.Tag = Theme.MutedTag;
            group.Controls.Add(_current);

            var note = new Label
            {
                Text = "O FPS máximo que você enxerga é limitado pela frequência do monitor (Hz). " +
                       "Depois de aplicar, você tem 15 segundos para confirmar; se a tela ficar preta, é só esperar que volta sozinho.",
                Location = new Point(2, 210),
                Size = new Size(530, 50),
                Tag = Theme.MutedTag
            };
            Controls.Add(group);
            Controls.Add(note);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // Listar todos os modos do driver é lento; só faz quando a aba é aberta.
            if (Visible && !_loaded) LoadScreens();
        }

        public void OnDisplaySettingsChanged()
        {
            if (_loaded) LoadScreens();
        }

        void LoadScreens()
        {
            _loaded = true;
            string previous = Device;
            _loading = true;
            _screens.Items.Clear();
            foreach (Screen s in Screen.AllScreens)
            {
                var item = new ScreenItem(s);
                _screens.Items.Add(item);
                if (s.DeviceName == previous || (previous == null && s.Primary)) _screens.SelectedItem = item;
            }
            if (_screens.SelectedIndex < 0 && _screens.Items.Count > 0) _screens.SelectedIndex = 0;
            _loading = false;
            LoadModes();
        }

        void LoadModes()
        {
            if (_loading || Device == null) return;
            _modes = DisplayModes.All(Device);
            DisplayModes.DevMode cur = DisplayModes.Current(Device);
            _current.Text = "Atual: " + cur.PelsWidth + " × " + cur.PelsHeight + " a " + cur.DisplayFrequency + " Hz";

            var sizes = new List<SizeItem>();
            foreach (var m in _modes)
                if (!sizes.Exists(x => x.Width == m.PelsWidth && x.Height == m.PelsHeight)) sizes.Add(new SizeItem(m.PelsWidth, m.PelsHeight));
            sizes.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));

            _loading = true;
            _resolution.BeginUpdate();
            _resolution.Items.Clear();
            foreach (var s in sizes)
            {
                _resolution.Items.Add(s);
                if (s.Width == cur.PelsWidth && s.Height == cur.PelsHeight) _resolution.SelectedItem = s;
            }
            _resolution.EndUpdate();
            _loading = false;
            LoadRefreshRates();
            SelectHz(cur.DisplayFrequency);
        }

        void LoadRefreshRates()
        {
            if (_loading) return;
            var size = _resolution.SelectedItem as SizeItem;
            _refresh.Items.Clear();
            if (size == null) return;
            var rates = new List<int>();
            foreach (var m in _modes)
                if (m.PelsWidth == size.Width && m.PelsHeight == size.Height && m.DisplayFrequency > 1 && !rates.Contains(m.DisplayFrequency))
                    rates.Add(m.DisplayFrequency);
            rates.Sort((a, b) => b.CompareTo(a));
            foreach (int hz in rates) _refresh.Items.Add(new HzItem(hz));
            if (_refresh.Items.Count > 0) _refresh.SelectedIndex = 0;
        }

        void SelectHz(int hz)
        {
            foreach (HzItem item in _refresh.Items)
                if (item.Hz == hz) { _refresh.SelectedItem = item; return; }
        }

        void SelectBest()
        {
            if (_resolution.Items.Count == 0) return;
            _resolution.SelectedIndex = 0;
            if (_refresh.Items.Count > 0) _refresh.SelectedIndex = 0;
        }

        void ApplySelected()
        {
            var size = _resolution.SelectedItem as SizeItem;
            var hz = _refresh.SelectedItem as HzItem;
            string device = Device;
            if (size == null || hz == null || device == null) return;

            DisplayModes.DevMode before = DisplayModes.Current(device);
            if (before.PelsWidth == size.Width && before.PelsHeight == size.Height && before.DisplayFrequency == hz.Hz) return;

            int result = DisplayModes.Apply(device, size.Width, size.Height, hz.Hz, before.BitsPerPel);
            if (result == DisplayModes.DispChangeRestart)
            {
                MessageBox.Show(this, "O Windows precisa reiniciar para aplicar esse modo.", "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (result != DisplayModes.DispChangeSuccessful)
            {
                MessageBox.Show(this, "O driver recusou esse modo (código " + result + ").", "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var confirm = new ConfirmModeDialog())
                if (confirm.ShowDialog(this) != DialogResult.OK)
                    DisplayModes.Apply(device, before.PelsWidth, before.PelsHeight, before.DisplayFrequency, before.BitsPerPel);
            LoadModes();
        }
    }

    /// <summary>Pergunta se o novo modo ficou bom; sem resposta em 15 s, volta ao anterior.</summary>
    sealed class ConfirmModeDialog : Form
    {
        readonly Timer _timer = new Timer { Interval = 1000 };
        readonly Label _text = new Label();
        int _seconds = 15;

        public ConfirmModeDialog()
        {
            Text = "ColorShift";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(360, 110);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            _text.Location = new Point(12, 12);
            _text.Size = new Size(336, 44);
            var keep = new Button { Text = "Manter", Location = new Point(152, 68), Size = new Size(94, 30), DialogResult = DialogResult.OK };
            var revert = new Button { Text = "Reverter", Location = new Point(254, 68), Size = new Size(94, 30), DialogResult = DialogResult.Cancel };
            AcceptButton = keep;
            CancelButton = revert;
            Controls.AddRange(new Control[] { _text, keep, revert });

            Theme.Apply(this);
            UpdateText();
            _timer.Tick += (s, e) =>
            {
                _seconds--;
                UpdateText();
                if (_seconds <= 0) { _timer.Stop(); DialogResult = DialogResult.Cancel; }
            };
            _timer.Start();
        }

        void UpdateText()
        {
            _text.Text = "Manter este modo de vídeo?\nVoltando ao anterior em " + _seconds + " s.";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
