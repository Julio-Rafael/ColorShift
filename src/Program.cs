using System;
using System.Threading;
using System.Windows.Forms;

namespace ColorShift
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var instance = new Mutex(true, "ColorShift.SingleInstance", out created))
            {
                // Reiniciado como administrador: espera a cópia anterior terminar de fechar.
                if (!created && Array.IndexOf(args, "--elevated-restart") >= 0)
                {
                    try { created = instance.WaitOne(15000); }
                    catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    MessageBox.Show("O ColorShift já está em execução (veja o ícone na bandeja).", "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                IColorController controller;
                try
                {
                    controller = ColorControllerFactory.Create();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "ColorShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                bool minimized = Array.Exists(args, a => a == "--minimized" || a == "-minimized");
                bool openRam = Array.IndexOf(args, "--ram") >= 0;
                Application.Run(new MainForm(controller, minimized, openRam));
            }
        }
    }
}
