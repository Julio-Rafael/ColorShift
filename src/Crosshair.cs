using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ColorShift
{
    public enum CrosshairStyle { Cruz, CruzComPonto, Ponto, Circulo, T }

    public class CrosshairSettings
    {
        public bool Enabled;
        public bool OnlyInGames = true;
        public CrosshairStyle Style = CrosshairStyle.Cruz;
        public int ColorArgb = Color.FromArgb(0, 255, 0).ToArgb();
        public int Length = 8;
        public int Thickness = 2;
        public int Gap = 3;
        public bool Outline = true;
        public int OffsetX;
        public int OffsetY;

        public Color Color
        {
            get { return Color.FromArgb(ColorArgb); }
        }
    }

    static class CrosshairRenderer
    {
        /// <summary>Metade do tamanho da área que a mira ocupa, a partir do centro.</summary>
        public static int Radius(CrosshairSettings s)
        {
            return s.Gap + s.Length + s.Thickness + 2;
        }

        /// <summary>Desenha sem antialiasing, com pixels inteiros: a janela usa cor-chave de transparência.</summary>
        public static void Draw(Graphics g, CrosshairSettings s, Point c)
        {
            Color color = s.Color;
            if (color.ToArgb() == CrosshairOverlay.KeyColor.ToArgb()) color = Color.FromArgb(255, 0, 254);
            int t = Math.Max(1, s.Thickness);
            int half = t / 2;
            var rects = new System.Collections.Generic.List<Rectangle>();

            bool arms = s.Style == CrosshairStyle.Cruz || s.Style == CrosshairStyle.CruzComPonto || s.Style == CrosshairStyle.T;
            if (arms)
            {
                int near = s.Gap, far = s.Gap + s.Length;
                rects.Add(new Rectangle(c.X - far, c.Y - half, far - near, t));     // esquerda
                rects.Add(new Rectangle(c.X + near + (t % 2), c.Y - half, far - near, t)); // direita
                rects.Add(new Rectangle(c.X - half, c.Y + near + (t % 2), t, far - near)); // baixo
                if (s.Style != CrosshairStyle.T)
                    rects.Add(new Rectangle(c.X - half, c.Y - far, t, far - near)); // cima
            }
            if (s.Style == CrosshairStyle.Ponto || s.Style == CrosshairStyle.CruzComPonto)
                rects.Add(new Rectangle(c.X - half, c.Y - half, t, t));

            if (s.Style == CrosshairStyle.Circulo)
            {
                int r = Math.Max(2, s.Gap + s.Length / 2);
                if (s.Outline)
                    using (var outline = new Pen(Color.Black, t + 2))
                        g.DrawEllipse(outline, c.X - r, c.Y - r, 2 * r, 2 * r);
                using (var pen = new Pen(color, t))
                    g.DrawEllipse(pen, c.X - r, c.Y - r, 2 * r, 2 * r);
                using (var dot = new SolidBrush(color))
                    g.FillRectangle(dot, c.X - half, c.Y - half, t, t);
                return;
            }

            if (s.Outline)
                using (var black = new SolidBrush(Color.Black))
                    foreach (var r in rects)
                        g.FillRectangle(black, Rectangle.Inflate(r, 1, 1));
            using (var brush = new SolidBrush(color))
                foreach (var r in rects)
                    g.FillRectangle(brush, r);
        }
    }

    /// <summary>
    /// Janela pequena, sem borda, sempre no topo e "transparente para cliques" (WS_EX_TRANSPARENT),
    /// centralizada no monitor. Não injeta nada no jogo; por isso não aparece em tela cheia exclusiva.
    /// </summary>
    sealed class CrosshairOverlay : Form
    {
        public static readonly Color KeyColor = Color.FromArgb(255, 0, 255);

        const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x8000000;
        const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10;
        static readonly IntPtr HwndTopmost = new IntPtr(-1);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        readonly Timer _topmostTimer = new Timer { Interval = 1000 };
        CrosshairSettings _settings;

        public CrosshairOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = KeyColor;
            TransparencyKey = KeyColor;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            // Jogos costumam se colocar por cima; reafirma a posição de topo periodicamente.
            _topmostTimer.Tick += (s, e) => SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;
                return cp;
            }
        }

        /// <summary>Mostra, esconde ou reposiciona a mira na tela informada.</summary>
        public void Place(CrosshairSettings settings, Screen screen, bool visible)
        {
            _settings = settings;
            if (!visible)
            {
                _topmostTimer.Stop();
                Hide();
                return;
            }
            int r = CrosshairRenderer.Radius(settings);
            int size = 2 * r + 1;
            Rectangle b = screen.Bounds;
            int cx = b.Left + b.Width / 2 + settings.OffsetX;
            int cy = b.Top + b.Height / 2 + settings.OffsetY;
            Bounds = new Rectangle(cx - r, cy - r, size, size);
            Invalidate();
            if (!Visible) Show();
            _topmostTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_settings == null) return;
            int r = CrosshairRenderer.Radius(_settings);
            CrosshairRenderer.Draw(e.Graphics, _settings, new Point(r, r));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _topmostTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Aba "Mira": opções e pré-visualização.</summary>
    sealed class CrosshairPage : UserControl
    {
        readonly CrosshairSettings _s;
        readonly Action _changed;
        readonly CheckBox _enabled = new CheckBox();
        readonly CheckBox _onlyInGames = new CheckBox();
        readonly CheckBox _outline = new CheckBox();
        readonly ComboBox _style = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button _color = new Button();
        readonly TrackBar _length = new TrackBar();
        readonly TrackBar _thickness = new TrackBar();
        readonly TrackBar _gap = new TrackBar();
        readonly NumericUpDown _offsetX = new NumericUpDown();
        readonly NumericUpDown _offsetY = new NumericUpDown();
        readonly Label _lengthValue = new Label(), _thicknessValue = new Label(), _gapValue = new Label();
        readonly Panel _preview = new Panel();
        bool _loading;

        static readonly string[] StyleNames = { "Cruz", "Cruz com ponto", "Ponto", "Círculo", "T" };
        static readonly Color[] Swatches = { Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 255), Color.FromArgb(255, 255, 0), Color.FromArgb(255, 0, 0), Color.White };

        public CrosshairPage(CrosshairSettings settings, Action changed)
        {
            _s = settings;
            _changed = changed;
            Font = new Font("Segoe UI", 9f);
            Size = new Size(536, 500);

            _enabled.Text = "Mostrar mira no centro da tela";
            _enabled.Location = new Point(2, 4);
            _enabled.AutoSize = true;
            _onlyInGames.Text = "Só quando um jogo com perfil estiver em foco";
            _onlyInGames.Location = new Point(2, 28);
            _onlyInGames.AutoSize = true;

            var look = new GroupBox { Text = "Aparência", Location = new Point(0, 56), Size = new Size(320, 300) };
            look.Controls.Add(new Label { Text = "Estilo", Location = new Point(10, 27), AutoSize = true });
            _style.Location = new Point(90, 23);
            _style.Width = 150;
            _style.Items.AddRange(StyleNames);
            look.Controls.Add(_style);

            look.Controls.Add(new Label { Text = "Cor", Location = new Point(10, 63), AutoSize = true });
            _color.Location = new Point(90, 58);
            _color.Size = new Size(40, 26);
            _color.FlatStyle = FlatStyle.Flat;
            _color.Tag = Theme.KeepTag;
            _color.Click += (s, e) => PickColor();
            look.Controls.Add(_color);
            for (int i = 0; i < Swatches.Length; i++)
            {
                Color swatch = Swatches[i];
                var b = new Button { Location = new Point(140 + i * 30, 60), Size = new Size(24, 22), BackColor = swatch, FlatStyle = FlatStyle.Flat, Tag = Theme.KeepTag };
                b.Click += (s, e) => { _s.ColorArgb = swatch.ToArgb(); OnChanged(); };
                look.Controls.Add(b);
            }

            AddSlider(look, "Tamanho", _length, _lengthValue, 98, 1, 40);
            AddSlider(look, "Espessura", _thickness, _thicknessValue, 138, 1, 10);
            AddSlider(look, "Espaço", _gap, _gapValue, 178, 0, 20);

            _outline.Text = "Contorno preto";
            _outline.Location = new Point(10, 222);
            _outline.AutoSize = true;
            look.Controls.Add(_outline);

            look.Controls.Add(new Label { Text = "Deslocar X / Y", Location = new Point(10, 258), AutoSize = true });
            foreach (var n in new[] { _offsetX, _offsetY })
            {
                n.Minimum = -300;
                n.Maximum = 300;
                n.Width = 64;
                n.ValueChanged += (s, e) => OnChanged();
                look.Controls.Add(n);
            }
            _offsetX.Location = new Point(110, 255);
            _offsetY.Location = new Point(180, 255);

            var previewGroup = new GroupBox { Text = "Pré-visualização", Location = new Point(330, 56), Size = new Size(206, 230) };
            _preview.Location = new Point(10, 22);
            _preview.Size = new Size(186, 196);
            _preview.BackColor = Color.FromArgb(70, 90, 110);
            _preview.Tag = Theme.KeepTag;
            _preview.Paint += (s, e) => CrosshairRenderer.Draw(e.Graphics, _s, new Point(_preview.Width / 2, _preview.Height / 2));
            previewGroup.Controls.Add(_preview);

            var reset = new Button { Text = "Restaurar padrão", Location = new Point(330, 296), Size = new Size(206, 30) };
            reset.Click += (s, e) => ResetDefaults();

            var note = new Label
            {
                Text = "A mira é uma janela por cima do jogo: funciona em \"janela sem bordas\" ou \"janela\". " +
                       "Em tela cheia exclusiva ela pode não aparecer. Confira as regras do jogo antes de usar em partidas ranqueadas.",
                Location = new Point(2, 366),
                Size = new Size(530, 50),
                Tag = Theme.MutedTag
            };

            Controls.AddRange(new Control[] { _enabled, _onlyInGames, look, previewGroup, reset, note });

            foreach (var c in new[] { _enabled, _onlyInGames, _outline }) c.CheckedChanged += (s, e) => OnChanged();
            _style.SelectedIndexChanged += (s, e) => OnChanged();
            LoadValues();
        }

        void AddSlider(GroupBox group, string text, TrackBar track, Label value, int top, int min, int max)
        {
            group.Controls.Add(new Label { Text = text, Location = new Point(10, top + 6), AutoSize = true });
            track.Location = new Point(84, top);
            track.Width = 190;
            track.Minimum = min;
            track.Maximum = max;
            track.TickStyle = TickStyle.None;
            track.AutoSize = false;
            track.Height = 30;
            track.ValueChanged += (s, e) => OnChanged();
            value.Location = new Point(280, top + 6);
            value.AutoSize = true;
            group.Controls.Add(track);
            group.Controls.Add(value);
        }

        /// <summary>Relê as opções depois de uma mudança feita fora da aba (ex.: menu da bandeja).</summary>
        public void Reload()
        {
            LoadValues();
        }

        void LoadValues()
        {
            _loading = true;
            _enabled.Checked = _s.Enabled;
            _onlyInGames.Checked = _s.OnlyInGames;
            _outline.Checked = _s.Outline;
            _style.SelectedIndex = Math.Max(0, Math.Min(StyleNames.Length - 1, (int)_s.Style));
            _length.Value = Math.Max(_length.Minimum, Math.Min(_length.Maximum, _s.Length));
            _thickness.Value = Math.Max(_thickness.Minimum, Math.Min(_thickness.Maximum, _s.Thickness));
            _gap.Value = Math.Max(_gap.Minimum, Math.Min(_gap.Maximum, _s.Gap));
            _offsetX.Value = Math.Max(-300, Math.Min(300, _s.OffsetX));
            _offsetY.Value = Math.Max(-300, Math.Min(300, _s.OffsetY));
            _loading = false;
            RefreshLabels();
        }

        void RefreshLabels()
        {
            _lengthValue.Text = _length.Value + " px";
            _thicknessValue.Text = _thickness.Value + " px";
            _gapValue.Text = _gap.Value + " px";
            _color.BackColor = _s.Color;
            _onlyInGames.Enabled = _enabled.Checked;
            _preview.Invalidate();
        }

        void OnChanged()
        {
            if (_loading) return;
            _s.Enabled = _enabled.Checked;
            _s.OnlyInGames = _onlyInGames.Checked;
            _s.Outline = _outline.Checked;
            _s.Style = (CrosshairStyle)Math.Max(0, _style.SelectedIndex);
            _s.Length = _length.Value;
            _s.Thickness = _thickness.Value;
            _s.Gap = _gap.Value;
            _s.OffsetX = (int)_offsetX.Value;
            _s.OffsetY = (int)_offsetY.Value;
            RefreshLabels();
            _changed();
        }

        void PickColor()
        {
            using (var dialog = new ColorDialog { Color = _s.Color, FullOpen = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _s.ColorArgb = Color.FromArgb(255, dialog.Color).ToArgb();
                    OnChanged();
                }
        }

        void ResetDefaults()
        {
            var d = new CrosshairSettings();
            bool enabled = _s.Enabled;
            _s.OnlyInGames = d.OnlyInGames;
            _s.Style = d.Style;
            _s.ColorArgb = d.ColorArgb;
            _s.Length = d.Length;
            _s.Thickness = d.Thickness;
            _s.Gap = d.Gap;
            _s.Outline = d.Outline;
            _s.OffsetX = d.OffsetX;
            _s.OffsetY = d.OffsetY;
            _s.Enabled = enabled;
            LoadValues();
            _changed();
        }
    }
}
