// Acesso ao SMBus pelo driver PawnIO, portado de i2c_smbus/Windows/i2c_smbus_pawnio.cpp do OpenRGB
// (SPDX-License-Identifier: GPL-2.0-or-later, Stephen Horvath / Adam Honse e colaboradores).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>
    /// Um barramento SMBus do chipset aberto pelo PawnIO. O PawnIO é um driver assinado que só executa módulos
    /// assinados; o módulo (SmbusI801.bin para Intel, SmbusPIIX4.bin para AMD) é quem fala com o hardware.
    /// </summary>
    sealed class PawnIoSmbus : IDisposable
    {
        const int SmbusRead = 1;
        const int SmbusWrite = 0;
        const int SizeByte = 1;
        const int SizeByteData = 2;
        const int SizeWordData = 3;
        const int SizeBlockData = 5;
        const int BlockMax = 32;

        // Mesmo mutex do OpenRGB, HWiNFO, SignalRGB etc.: impede que dois programas misturem transações no barramento.
        const string GlobalMutexName = "Global\\Access_SMBUS.HTP.Method";
        const int SleepModeAlwaysSleep = 2;
        const int AccessDenied = unchecked((int)0x80070005);

        [DllImport("PawnIOLib.dll")] static extern int pawnio_version(out uint version);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_open(out IntPtr handle);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
        [DllImport("PawnIOLib.dll")] static extern int pawnio_close(IntPtr handle);
        [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern int pawnio_execute(IntPtr handle, string name, ulong[] input, UIntPtr inSize,
            ulong[] output, UIntPtr outSize, out UIntPtr returnSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        static bool _libraryLoaded;

        IntPtr _handle;
        readonly Mutex _globalMutex;
        readonly object _lock = new object();

        public string Name { get; private set; }
        public int PciVendor { get; private set; }
        public int PciDevice { get; private set; }

        PawnIoSmbus(IntPtr handle, string name)
        {
            _handle = handle;
            Name = name;
            ReadIdentity();
            Execute("ioctl_set_sleep_mode", new ulong[] { SleepModeAlwaysSleep }, 0); // o módulo pode não ter; ignora erro
            try
            {
                bool created;
                _globalMutex = new Mutex(false, GlobalMutexName, out created);
            }
            catch (UnauthorizedAccessException) { _globalMutex = null; }
        }

        /// <summary>Carrega o PawnIOLib.dll instalado pelo PawnIO (ou ao lado do exe). Falha se o driver não está instalado.</summary>
        public static void EnsureLibrary()
        {
            if (_libraryLoaded) return;
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            var candidates = new[]
            {
                Path.Combine(appDir, "PawnIOLib.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll")
            };
            foreach (string path in candidates)
            {
                if (File.Exists(path) && LoadLibrary(path) != IntPtr.Zero)
                {
                    uint version;
                    if (pawnio_version(out version) == 0)
                    {
                        _libraryLoaded = true;
                        return;
                    }
                }
            }
            throw new PawnIoException("O driver PawnIO não está instalado. Instale-o em https://pawnio.eu e tente de novo.");
        }

        /// <summary>Abre os barramentos de memória que existirem nesta placa (mesma ordem do OpenRGB).</summary>
        public static List<PawnIoSmbus> OpenDramBuses()
        {
            EnsureLibrary();
            var buses = new List<PawnIoSmbus>();
            IntPtr handle;

            if (TryStart("SmbusI801.bin", out handle))
                buses.Add(new PawnIoSmbus(handle, "i801"));

            for (int port = 0; port < 2; port++)
            {
                if (!TryStart("SmbusPIIX4.bin", out handle)) continue;
                var output = new ulong[1];
                UIntPtr returned;
                if (pawnio_execute(handle, "ioctl_piix4_port_sel", new ulong[] { (ulong)port }, (UIntPtr)1u,
                        output, (UIntPtr)1u, out returned) == 0)
                    buses.Add(new PawnIoSmbus(handle, "piix4_" + port));
                else
                    pawnio_close(handle);
            }
            return buses;
        }

        static bool TryStart(string module, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            string path = FindModule(module);
            if (path == null) return false;
            byte[] blob = File.ReadAllBytes(path);

            int status = pawnio_open(out handle);
            if (status == AccessDenied)
                throw new PawnIoException("Sem permissão para abrir o PawnIO. Execute o ColorShift como administrador.");
            if (status != 0)
                throw new PawnIoException("Não foi possível abrir o driver PawnIO (0x" + status.ToString("X8") + ").");

            status = pawnio_load(handle, blob, (UIntPtr)(uint)blob.Length);
            if (status != 0)
            {
                // ERROR_NOT_SUPPORTED = este chipset não é desse fabricante; é o caso normal de um dos dois módulos.
                pawnio_close(handle);
                handle = IntPtr.Zero;
                return false;
            }
            return true;
        }

        static string FindModule(string module)
        {
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string path in new[] { Path.Combine(appDir, module), Path.Combine(appDir, "pawnio", module) })
                if (File.Exists(path)) return path;
            return null;
        }

        void ReadIdentity()
        {
            var output = new ulong[3];
            if (Execute("ioctl_identity", new ulong[] { 0 }, output) != 0) return;
            PciVendor = (int)(output[2] & 0xFFFF);
            PciDevice = (int)((output[2] >> 16) & 0xFFFF);
        }

        int Execute(string function, ulong[] input, int outputCount)
        {
            return Execute(function, input, new ulong[Math.Max(1, outputCount)]);
        }

        int Execute(string function, ulong[] input, ulong[] output)
        {
            UIntPtr returned;
            return pawnio_execute(_handle, function, input, (UIntPtr)(uint)input.Length, output,
                (UIntPtr)(uint)output.Length, out returned);
        }

        /// <summary>
        /// Uma transação SMBus. <paramref name="data"/> é o i2c_smbus_data do Linux (34 bytes: byte, word ou bloco
        /// com o tamanho em [0]). Retorna false se o dispositivo não respondeu.
        /// </summary>
        bool Transfer(int address, int readWrite, int command, int size, byte[] data)
        {
            var input = new ulong[9];
            input[0] = (ulong)address;
            input[1] = (ulong)readWrite;
            input[2] = (ulong)command;
            input[3] = (ulong)size;
            if (data != null) Buffer.BlockCopy(data, 0, input, 4 * 8, Math.Min(data.Length, 5 * 8));
            var output = new ulong[5];

            int status;
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) throw new ObjectDisposedException("PawnIoSmbus");
                bool owned = false;
                if (_globalMutex != null)
                {
                    try { owned = _globalMutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new PawnIoException("O SMBus está ocupado por outro programa.");
                }
                try { status = Execute("ioctl_smbus_xfer", input, output); }
                finally { if (owned) _globalMutex.ReleaseMutex(); }
            }

            if (data != null) Buffer.BlockCopy(output, 0, data, 0, Math.Min(data.Length, 5 * 8));
            return status == 0;
        }

        /// <summary>Lê um byte sem comando (usado para ver se há algo no endereço). -1 se ninguém respondeu.</summary>
        public int ReadByte(int address)
        {
            var data = new byte[34];
            return Transfer(address, SmbusRead, 0, SizeByte, data) ? data[0] : -1;
        }

        public int ReadByteData(int address, int command)
        {
            var data = new byte[34];
            return Transfer(address, SmbusRead, command, SizeByteData, data) ? data[0] : -1;
        }

        public bool WriteByteData(int address, int command, byte value)
        {
            var data = new byte[34];
            data[0] = value;
            return Transfer(address, SmbusWrite, command, SizeByteData, data);
        }

        public bool WriteWordData(int address, int command, ushort value)
        {
            var data = new byte[34];
            data[0] = (byte)value;
            data[1] = (byte)(value >> 8);
            return Transfer(address, SmbusWrite, command, SizeWordData, data);
        }

        public bool WriteBlockData(int address, int command, byte[] values, int offset, int length)
        {
            length = Math.Min(length, BlockMax);
            var data = new byte[34];
            data[0] = (byte)length;
            Array.Copy(values, offset, data, 1, length);
            return Transfer(address, SmbusWrite, command, SizeBlockData, data);
        }

        public override string ToString()
        {
            return "SMBus " + Name;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) pawnio_close(_handle);
                _handle = IntPtr.Zero;
            }
            if (_globalMutex != null) _globalMutex.Dispose();
        }
    }

    sealed class PawnIoException : Exception
    {
        public PawnIoException(string message) : base(message) { }
    }
}
