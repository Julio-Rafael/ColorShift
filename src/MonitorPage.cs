using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>
    /// Aba "Monitor": ajustes de imagem e cor gravados no próprio monitor por DDC/CI,
    /// como no menu (OSD) do monitor. Só aparecem os controles que o monitor informa suportar.
    /// O DDC/CI é lento (a string de capacidades leva segundos, cada leitura dezenas de ms), então toda a
    /// conversa com o monitor roda fora da thread da interface, em uma fila, para os comandos chegarem na ordem.
    /// </summary>
    sealed class MonitorPage : UserControl
    {
        sealed class VcpRow
        {
            public byte Code;
            public Label Caption;
            public TrackBar Track;
            public Label Value;
        }

        sealed class Reading
        {
            public readonly Dictionary<byte, KeyValuePair<int, int>> Values = new Dictionary<byte, KeyValuePair<int, int>>();
            public bool HasPreset;
            public int Preset;
        }

        readonly ComboBox _monitors = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label _info = new Label();
        readonly ComboBox _preset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label _saturationNote = new Label();
        readonly Button _refresh;
        readonly List<VcpRow> _rows = new List<VcpRow>();
        readonly Dictionary<byte, int> _pending = new Dictionary<byte, int>();
        readonly Timer _writeTimer = new Timer { Interval = 100 };
        readonly Timer _reloadTimer = new Timer { Interval = 1500 };
        readonly Button _resetAll;
        readonly Button _resetColor;

        // Só a fila de trabalho chama o DDC/CI (TryGet/Set/Dispose). A interface só lê os dados fixos do monitor.
        Task _queue = Task.FromResult(0);
        List<DdcMonitor> _list = new List<DdcMonitor>();
        int _readGeneration;
        bool _loaded;
        bool _loading;

        DdcMonitor Current
        {
            get { return _monitors.SelectedItem as DdcMonitor; }
        }

        public MonitorPage()
        {
            Font = new Font("Segoe UI", 9f);
            Size = new Size(536, 500);

            var caption = new Label { Text = "Monitor:", Location = new Point(2, 9), AutoSize = true };
            _monitors.Location = new Point(62, 5);
            _monitors.Width = 330;
            _monitors.SelectedIndexChanged += (s, e) => ReadValues();
            _refresh = new Button { Text = "Atualizar", Location = new Point(400, 4), Size = new Size(126, 26) };
            _refresh.Click += (s, e) => Reload();
            _info.Location = new Point(2, 36);
            _info.Size = new Size(530, 18);
            _info.Tag = Theme.MutedTag;

            var picture = new GroupBox { Text = "Ajuste de imagem", Location = new Point(0, 58), Size = new Size(536, 140) };
            AddRow(picture, Vcp.Brightness, "Brilho", 22);
            AddRow(picture, Vcp.Contrast, "Contraste", 60);
            AddRow(picture, Vcp.Sharpness, "Nitidez", 98);

            var color = new GroupBox { Text = "Ajuste de cor", Location = new Point(0, 204), Size = new Size(536, 200) };
            var presetCaption = new Label { Text = "Predefinição", Location = new Point(10, 27), AutoSize = true };
            _preset.Location = new Point(110, 23);
            _preset.Width = 220;
            _preset.SelectedIndexChanged += OnPresetChanged;
            color.Controls.Add(presetCaption);
            color.Controls.Add(_preset);
            AddRow(color, Vcp.RedGain, "Vermelho", 56);
            AddRow(color, Vcp.GreenGain, "Verde", 90);
            AddRow(color, Vcp.BlueGain, "Azul", 124);
            AddRow(color, Vcp.Saturation, "Saturação", 158);
            _saturationNote.Location = new Point(10, 162);
            _saturationNote.Size = new Size(516, 32);
            _saturationNote.Tag = Theme.MutedTag;
            _saturationNote.Text = "Este monitor não tem saturação por hardware. Use a aba \"Saturação\", que ajusta pela placa de vídeo.";
            _saturationNote.Visible = false;
            color.Controls.Add(_saturationNote);

            var factory = new GroupBox { Text = "Padrão de fábrica", Location = new Point(0, 410), Size = new Size(536, 64) };
            _resetAll = new Button { Text = "Restaurar tudo de fábrica", Location = new Point(10, 22), Size = new Size(200, 30) };
            _resetColor = new Button { Text = "Restaurar só as cores", Location = new Point(220, 22), Size = new Size(170, 30) };
            _resetAll.Click += (s, e) => FactoryReset(Vcp.FactoryDefaults, "todas as configurações (imagem e cor)");
            _resetColor.Click += (s, e) => FactoryReset(Vcp.FactoryColorDefaults, "as configurações de cor");
            factory.Controls.Add(_resetAll);
            factory.Controls.Add(_resetColor);

            Controls.AddRange(new Control[] { caption, _monitors, _refresh, _info, picture, color, factory });

            _writeTimer.Tick += (s, e) => FlushWrites();
            _reloadTimer.Tick += (s, e) => { _reloadTimer.Stop(); ReadValues(); };
            DisableControls();
        }

        void AddRow(GroupBox group, byte code, string text, int top)
        {
            var row = new VcpRow
            {
                Code = code,
                Caption = new Label { Text = text, Location = new Point(10, top + 6), AutoSize = true },
                Track = new TrackBar { Location = new Point(104, top), Width = 360, TickStyle = TickStyle.None, AutoSize = false, Height = 30 },
                Value = new Label { Location = new Point(470, top + 6), AutoSize = true, Text = "—" }
            };
            row.Track.ValueChanged += (s, e) => OnRowChanged(row);
            group.Controls.Add(row.Caption);
            group.Controls.Add(row.Track);
            group.Controls.Add(row.Value);
            _rows.Add(row);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            // Só conversa com o monitor quando a aba é aberta.
            if (Visible && !_loaded) Reload();
        }

        /// <summary>Chamado quando a configuração de vídeo muda: os handles dos monitores físicos deixam de valer.</summary>
        public void OnDisplaySettingsChanged()
        {
            if (_loaded) Reload();
        }

        /// <summary>Executa em segundo plano, um trabalho por vez; "done" volta para a thread da interface.</summary>
        void Enqueue(Action work, Action done)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception) { } // falha de DDC/CI: a interface mostra o que conseguiu ler
                if (done == null || IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)(() => { if (!IsDisposed) done(); })); }
                catch (InvalidOperationException) { }
            }, TaskScheduler.Default);
        }

        void DisposeLater(List<DdcMonitor> monitors)
        {
            if (monitors.Count == 0) return;
            Enqueue(() => { foreach (var m in monitors) m.Dispose(); }, null);
        }

        /// <summary>Bloqueia os controles enquanto o monitor está sendo lido.</summary>
        void DisableControls()
        {
            foreach (var row in _rows)
            {
                row.Track.Enabled = false;
                row.Caption.Enabled = false;
            }
            _preset.Enabled = false;
            _resetAll.Enabled = false;
            _resetColor.Enabled = false;
        }

        void Reload()
        {
            if (!IsHandleCreated) CreateHandle();
            FlushWrites();
            _loaded = true;
            string previous = Current == null ? null : Current.Device;

            DisposeLater(_list);
            _list = new List<DdcMonitor>();
            _readGeneration++;
            _loading = true;
            _monitors.Items.Clear();
            _loading = false;
            _monitors.Enabled = false;
            _refresh.Enabled = false;
            DisableControls();
            _info.Text = "Procurando monitores por DDC/CI…";

            var found = new List<DdcMonitor>();
            Enqueue(() => found = DdcMonitor.EnumerateAll(), () =>
            {
                DisposeLater(_list); // outra busca terminou antes desta
                _list = found;
                _refresh.Enabled = true;
                _monitors.Enabled = _list.Count > 0;
                _loading = true;
                foreach (var m in _list) _monitors.Items.Add(m);
                _loading = false;

                int index = Math.Max(0, _list.FindIndex(m => m.Device == previous));
                if (_list.Count > 0) _monitors.SelectedIndex = index; // dispara ReadValues
                else ReadValues();
            });
        }

        void ReadValues()
        {
            if (_loading) return;
            FlushWrites();
            DdcMonitor m = Current;
            int generation = ++_readGeneration;

            if (m == null)
            {
                ShowValues(null, new Reading());
                return;
            }

            _info.Text = "Lendo os ajustes de " + m.Model + "…";
            DisableControls();

            var reading = new Reading();
            var codes = new List<byte>();
            foreach (var row in _rows) codes.Add(row.Code);
            Enqueue(() =>
            {
                foreach (byte code in codes)
                {
                    int cur, max;
                    if (m.Supports(code) && m.TryGet(code, out cur, out max) && max > 0)
                        reading.Values[code] = new KeyValuePair<int, int>(cur, max);
                }
                int presetCur, presetMax;
                if (m.Supports(Vcp.ColorPreset) && m.TryGet(Vcp.ColorPreset, out presetCur, out presetMax))
                {
                    reading.HasPreset = true;
                    reading.Preset = presetCur;
                }
            }, () =>
            {
                // Descarta leituras antigas (o usuário trocou de monitor ou recarregou no meio).
                if (generation == _readGeneration && m == Current) ShowValues(m, reading);
            });
        }

        void ShowValues(DdcMonitor m, Reading reading)
        {
            _loading = true;
            _info.Text = m == null
                ? "Nenhum monitor respondeu por DDC/CI. Verifique se o DDC/CI está ligado no menu do monitor."
                : "Controle por DDC/CI" + (m.MccsVersion == null ? "" : " (MCCS " + m.MccsVersion + ")") + ". As mudanças ficam salvas no monitor.";

            foreach (var row in _rows)
            {
                KeyValuePair<int, int> v;
                bool ok = reading.Values.TryGetValue(row.Code, out v);
                int cur = ok ? v.Key : 0, max = ok ? v.Value : 100;
                row.Track.Maximum = max;
                row.Track.Value = Math.Min(cur, max);
                row.Track.LargeChange = Math.Max(1, max / 10);
                row.Value.Text = ok ? cur.ToString() : "—";
                row.Track.Enabled = ok;
                row.Caption.Enabled = ok;
                bool hide = row.Code == Vcp.Saturation && !ok;
                row.Caption.Visible = row.Track.Visible = row.Value.Visible = !hide;
                if (row.Code == Vcp.Saturation) _saturationNote.Visible = hide;
            }

            _preset.BeginUpdate();
            _preset.Items.Clear();
            if (m != null && reading.HasPreset)
                foreach (int value in m.ValuesOf(Vcp.ColorPreset))
                {
                    var item = new PresetItem(value);
                    _preset.Items.Add(item);
                    if (value == reading.Preset) _preset.SelectedItem = item;
                }
            _preset.EndUpdate();
            _preset.Enabled = _preset.Items.Count > 0;

            _resetAll.Enabled = m != null && m.Supports(Vcp.FactoryDefaults);
            _resetColor.Enabled = m != null && m.Supports(Vcp.FactoryColorDefaults);
            _loading = false;
        }

        sealed class PresetItem
        {
            public readonly int Value;
            public PresetItem(int value) { Value = value; }
            public override string ToString() { return Vcp.PresetName(Value); }
        }

        void OnRowChanged(VcpRow row)
        {
            row.Value.Text = row.Track.Value.ToString();
            if (_loading) return;
            bool isGain = row.Code == Vcp.RedGain || row.Code == Vcp.GreenGain || row.Code == Vcp.BlueGain;
            if (isGain) SelectUserPreset();
            _pending[row.Code] = row.Track.Value;
            _writeTimer.Stop();
            _writeTimer.Start();
        }

        /// <summary>O ganho RGB só vale na predefinição "Usuário"; troca para ela ao mexer nas cores.</summary>
        void SelectUserPreset()
        {
            var selected = _preset.SelectedItem as PresetItem;
            if (selected != null && selected.Value == Vcp.PresetUser1) return;
            foreach (PresetItem item in _preset.Items)
                if (item.Value == Vcp.PresetUser1) { _preset.SelectedItem = item; return; }
        }

        void OnPresetChanged(object sender, EventArgs e)
        {
            var item = _preset.SelectedItem as PresetItem;
            DdcMonitor m = Current;
            if (_loading || item == null || m == null) return;
            FlushWrites();
            int value = item.Value;
            Enqueue(() => m.Set(Vcp.ColorPreset, value), null);
            // A predefinição muda os ganhos RGB; relê depois que o monitor aplicar.
            _reloadTimer.Stop();
            _reloadTimer.Start();
        }

        void FlushWrites()
        {
            _writeTimer.Stop();
            DdcMonitor m = Current;
            if (m != null && _pending.Count > 0)
            {
                var writes = new List<KeyValuePair<byte, int>>(_pending);
                Enqueue(() => { foreach (var w in writes) m.Set(w.Key, w.Value); }, null);
            }
            _pending.Clear();
        }

        void FactoryReset(byte code, string what)
        {
            DdcMonitor m = Current;
            if (m == null) return;
            var answer = MessageBox.Show(this, "Voltar " + what + " de " + m.Model + " para o padrão de fábrica?",
                "ColorShift", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            _writeTimer.Stop();
            _pending.Clear();
            bool ok = false;
            Enqueue(() => ok = m.Set(code, 1), () =>
            {
                if (!ok)
                    MessageBox.Show(this, "O monitor não aceitou o comando.", "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushWrites();
                _writeTimer.Dispose();
                _reloadTimer.Dispose();
                DisposeLater(_list);
                _list = new List<DdcMonitor>();
                // Espera as escritas pendentes chegarem ao monitor antes de fechar.
                try { _queue.Wait(3000); } catch (AggregateException) { }
            }
            base.Dispose(disposing);
        }
    }
}
