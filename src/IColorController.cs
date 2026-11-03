using System;
using System.Collections.Generic;

namespace ColorShift
{
    /// <summary>
    /// Abstração do controle de saturação/vibrance de uma GPU.
    /// Os níveis são expressos em porcentagem: 50% = padrão do driver, 100% = máximo, 0% = mínimo.
    /// </summary>
    interface IColorController : IDisposable
    {
        string Description { get; }

        /// <summary>Menor porcentagem que o driver consegue aplicar (NVIDIA não vai abaixo do padrão).</summary>
        int MinPercent { get; }

        /// <summary>Nomes de dispositivo dos monitores controláveis (ex.: \\.\DISPLAY1).</summary>
        IList<string> Displays { get; }

        void Refresh();
        int GetPercent(string display);
        bool SetPercent(string display, int percent);

        void SaveOriginal();
        void RestoreOriginal();

        /// <summary>Converte um nível bruto salvo pelo vibranceGUI em porcentagem.</summary>
        int LegacyRawToPercent(int raw);
    }

    static class LevelMap
    {
        public static int ToRaw(int percent, int min, int def, int max)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            if (percent >= 50)
                return def + (int)Math.Round((max - def) * (percent - 50) / 50.0);
            return min + (int)Math.Round((def - min) * percent / 50.0);
        }

        public static int ToPercent(int raw, int min, int def, int max)
        {
            if (raw >= def)
                return max == def ? 50 : 50 + (int)Math.Round(50.0 * (raw - def) / (max - def));
            return def == min ? 50 : (int)Math.Round(50.0 * (raw - min) / (def - min));
        }
    }

    static class ColorControllerFactory
    {
        public static IColorController Create()
        {
            var errors = new List<string>();

            try
            {
                var nvidia = new NvidiaController();
                if (nvidia.Displays.Count > 0) return nvidia;
                nvidia.Dispose();
                errors.Add("NVIDIA: nenhum monitor conectado à GPU NVIDIA.");
            }
            catch (Exception ex) { errors.Add("NVIDIA: " + ex.Message); }

            try
            {
                var amd = new AmdController();
                if (amd.Displays.Count > 0) return amd;
                amd.Dispose();
                errors.Add("AMD: nenhum monitor conectado à GPU AMD.");
            }
            catch (Exception ex) { errors.Add("AMD: " + ex.Message); }

            throw new InvalidOperationException(
                "Nenhuma GPU NVIDIA ou AMD compatível foi encontrada.\n\n" + string.Join("\n", errors.ToArray()));
        }
    }
}
