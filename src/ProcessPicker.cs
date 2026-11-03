using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>Lista os processos com janela aberta para criar um perfil rapidamente.</summary>
    sealed class ProcessPicker : Form
    {
        readonly ListView _list = new ListView();

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        const uint GwOwner = 4;

        public string SelectedPath { get; private set; }

        public ProcessPicker()
        {
            Text = "Processos abertos";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 400);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.Columns.Add("Processo", 150);
            _list.Columns.Add("Executável", 340);
            _list.DoubleClick += (s, e) => Accept();

            var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(6) };
            var ok = new Button { Text = "Adicionar", Width = 100 };
            var cancel = new Button { Text = "Cancelar", Width = 100, DialogResult = DialogResult.Cancel };
            var reload = new Button { Text = "Atualizar", Width = 100 };
            ok.Click += (s, e) => Accept();
            reload.Click += (s, e) => LoadProcesses();
            panel.Controls.AddRange(new Control[] { cancel, ok, reload });
            CancelButton = cancel;

            Controls.Add(_list);
            Controls.Add(panel);
            Theme.Apply(this);
            LoadProcesses();
        }

        void LoadProcesses()
        {
            // Uma passada só pelas janelas (Process.MainWindowHandle varre todas as janelas para cada processo).
            uint own = (uint)Process.GetCurrentProcess().Id;
            var pids = new HashSet<uint>();
            EnumWindows((hwnd, data) =>
            {
                if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == IntPtr.Zero)
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid != 0 && pid != own) pids.Add(pid);
                }
                return true;
            }, IntPtr.Zero);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<ListViewItem>();
            foreach (uint pid in pids)
            {
                string path = ForegroundWatcher.GetProcessPath(pid);
                if (path == null || !seen.Add(path)) continue;
                rows.Add(new ListViewItem(new[] { Path.GetFileNameWithoutExtension(path), path }) { Tag = path });
            }
            rows.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Items.AddRange(rows.ToArray());
            _list.EndUpdate();
        }

        void Accept()
        {
            if (_list.SelectedItems.Count == 0) return;
            SelectedPath = (string)_list.SelectedItems[0].Tag;
            DialogResult = DialogResult.OK;
        }
    }
}
