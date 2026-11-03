using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ColorShift
{
    /// <summary>
    /// Tema claro/escuro. Os controles WinForms não têm modo escuro nativo, então as cores são aplicadas
    /// recursivamente e o que o sistema desenha sozinho (abas, cabeçalho de lista, borda de grupo) é pintado aqui.
    /// Controles com Tag = MutedTag usam a cor de texto secundária; com Tag = KeepTag não são tocados.
    /// </summary>
    static class Theme
    {
        public const string MutedTag = "muted";
        public const string KeepTag = "keep";
        public static readonly string[] Modes = { "Sistema", "Claro", "Escuro" };

        public static bool Dark { get; private set; }

        public static Color Back { get { return Dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control; } }
        public static Color Surface { get { return Dark ? Color.FromArgb(45, 45, 45) : SystemColors.Window; } }
        public static Color Fore { get { return Dark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText; } }
        public static Color Muted { get { return Dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText; } }
        public static Color Border { get { return Dark ? Color.FromArgb(70, 70, 70) : SystemColors.ControlDark; } }
        public static Color Accent { get { return Color.FromArgb(0, 120, 212); } }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        const int DwmUseImmersiveDarkMode = 20;

        static readonly HashSet<Control> Hooked = new HashSet<Control>();

        public static void SetMode(string mode)
        {
            Dark = mode == "Escuro" || (mode != "Claro" && SystemUsesDarkTheme());
        }

        static bool SystemUsesDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void Apply(Control root)
        {
            var form = root as Form;
            if (form != null)
            {
                if (form.IsHandleCreated) ApplyTitleBar(form);
                else form.HandleCreated += (s, e) => ApplyTitleBar((Form)s);
            }
            ApplyTree(root);
            root.Invalidate(true);
        }

        public static void Apply(ToolStrip strip)
        {
            strip.Renderer = Dark ? (ToolStripRenderer)new DarkRenderer() : new ToolStripProfessionalRenderer();
            strip.BackColor = Back;
            strip.ForeColor = Fore;
            foreach (ToolStripItem item in strip.Items) item.ForeColor = Fore;
        }

        static void ApplyTitleBar(Form form)
        {
            int value = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }

        static void ApplyTree(Control c)
        {
            if (Equals(c.Tag, KeepTag)) return;
            Color fore = Equals(c.Tag, MutedTag) ? Muted : Fore;

            if (c is Button)
            {
                var b = (Button)c;
                b.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
                b.FlatAppearance.BorderColor = Border;
                b.BackColor = Dark ? Surface : SystemColors.Control;
                b.UseVisualStyleBackColor = !Dark;
                b.ForeColor = fore;
            }
            else if (c is ComboBox)
            {
                // "DarkMode_CFD" é o tema escuro nativo de caixa de seleção do Windows 10/11.
                var cb = (ComboBox)c;
                cb.BackColor = Surface;
                cb.ForeColor = fore;
                if (cb.IsHandleCreated) SetWindowTheme(cb.Handle, Dark ? "DarkMode_CFD" : null, null);
                else if (!Hooked.Contains(cb))
                {
                    Hook(cb);
                    cb.HandleCreated += (s, e) => { if (Dark) SetWindowTheme(((ComboBox)s).Handle, "DarkMode_CFD", null); };
                }
            }
            else if (c is ListView)
            {
                var lv = (ListView)c;
                lv.BackColor = Surface;
                lv.ForeColor = fore;
                HookListView(lv);
                if (lv.IsHandleCreated) SetWindowTheme(lv.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            else if (c is NumericUpDown)
            {
                var n = (NumericUpDown)c;
                n.BorderStyle = Dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                n.BackColor = Surface;
                n.ForeColor = fore;
            }
            else if (c is TextBox)
            {
                c.BackColor = Surface;
                c.ForeColor = fore;
            }
            else if (c is TabPage)
            {
                ((TabPage)c).UseVisualStyleBackColor = false;
                c.BackColor = Back;
                c.ForeColor = fore;
            }
            else if (c is GroupBox)
            {
                c.BackColor = Back;
                c.ForeColor = fore;
                HookGroupBox((GroupBox)c);
            }
            else if (c is ToolStrip)
            {
                Apply((ToolStrip)c);
            }
            else
            {
                c.BackColor = Back;
                c.ForeColor = fore;
            }

            foreach (Control child in c.Controls) ApplyTree(child);
        }

        static void Hook(Control c)
        {
            Hooked.Add(c);
            c.Disposed += (s, e) => Hooked.Remove((Control)s);
        }

        /// <summary>O cabeçalho da lista é desenhado pelo sistema sempre claro; no escuro ele é pintado aqui.</summary>
        static void HookListView(ListView lv)
        {
            if (Hooked.Contains(lv)) return;
            Hook(lv);
            lv.OwnerDraw = true;
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                if (!Dark) { e.DrawDefault = true; return; }
                using (var back = new SolidBrush(Color.FromArgb(52, 52, 52)))
                    e.Graphics.FillRectangle(back, e.Bounds);
                using (var line = new Pen(Border))
                    e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                var text = Rectangle.Inflate(e.Bounds, -6, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, text, Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            lv.HandleCreated += (s, e) => SetWindowTheme(((ListView)s).Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
        }

        /// <summary>A borda do GroupBox ignora as cores; no escuro ela é redesenhada por cima.</summary>
        static void HookGroupBox(GroupBox g)
        {
            if (Hooked.Contains(g)) return;
            Hook(g);
            g.Paint += (s, e) =>
            {
                if (!Dark) return;
                var box = (GroupBox)s;
                Size textSize = TextRenderer.MeasureText(box.Text, box.Font);
                e.Graphics.Clear(box.BackColor);
                int top = textSize.Height / 2;
                using (var pen = new Pen(Border))
                    e.Graphics.DrawRectangle(pen, 0, top, box.Width - 1, box.Height - top - 1);
                var textRect = new Rectangle(6, 0, textSize.Width + 2, textSize.Height);
                using (var back = new SolidBrush(box.BackColor))
                    e.Graphics.FillRectangle(back, textRect);
                TextRenderer.DrawText(e.Graphics, box.Text, box.Font, textRect, Fore);
            };
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(32, 32, 32);
            static readonly Color Menu = Color.FromArgb(43, 43, 43);
            static readonly Color Hover = Color.FromArgb(65, 65, 65);
            public override Color ToolStripDropDownBackground { get { return Menu; } }
            public override Color MenuBorder { get { return Color.FromArgb(70, 70, 70); } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color ImageMarginGradientBegin { get { return Menu; } }
            public override Color ImageMarginGradientMiddle { get { return Menu; } }
            public override Color ImageMarginGradientEnd { get { return Menu; } }
            public override Color SeparatorDark { get { return Color.FromArgb(70, 70, 70); } }
            public override Color SeparatorLight { get { return Menu; } }
            public override Color StatusStripGradientBegin { get { return Bg; } }
            public override Color StatusStripGradientEnd { get { return Bg; } }
            public override Color CheckBackground { get { return Hover; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
        }

        sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Color.FromArgb(240, 240, 240);
                base.OnRenderItemText(e);
            }
        }
    }

    /// <summary>TabControl com as abas pintadas pelo app, para acompanhar o tema (o padrão é sempre claro).</summary>
    sealed class ThemedTabControl : TabControl
    {
        public ThemedTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Point(14, 5);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parentBack = Parent == null ? Theme.Back : Parent.BackColor;
            g.Clear(parentBack);

            Rectangle page = DisplayRectangle;
            page.Inflate(4, 4);
            using (var back = new SolidBrush(Theme.Back))
                g.FillRectangle(back, page);
            using (var border = new Pen(Theme.Border))
                g.DrawRectangle(border, page.X, page.Y, page.Width - 1, page.Height - 1);

            for (int i = 0; i < TabCount; i++)
            {
                Rectangle r = GetTabRect(i);
                bool selected = i == SelectedIndex;
                Color fill = selected ? Theme.Back : (Theme.Dark ? Color.FromArgb(24, 24, 24) : SystemColors.ControlLight);
                using (var brush = new SolidBrush(fill))
                    g.FillRectangle(brush, r);
                using (var border = new Pen(Theme.Border))
                    g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height);
                if (selected)
                    using (var accent = new SolidBrush(Theme.Accent))
                        g.FillRectangle(accent, r.X, r.Y, r.Width, 2);
                TextRenderer.DrawText(g, TabPages[i].Text, Font, r, selected ? Theme.Fore : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
