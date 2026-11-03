// Acesso ao SMBus do chipset pelo driver PawnIO (https://pawnio.eu).
// Usa apenas a API pública do PawnIOLib.dll e as funções que os módulos assinados SmbusI801.bin / SmbusPIIX4.bin
// exportam (ioctl_smbus_xfer, ioctl_set_sleep_mode, ioctl_piix4_port_sel), lidas dos próprios módulos.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ColorShift
{
    /// <summary>Chamadas nativas do PawnIOLib.dll. Todas retornam um HRESULT (0 = sucesso).</summary>
    static class PawnIoNative
    {
        const string Dll = "PawnIOLib.dll";

        [DllImport(Dll)] public static extern int pawnio_version(out uint version);
        [DllImport(Dll)] public static extern int pawnio_open(out IntPtr handle);
        [DllImport(Dll)] public static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
        [DllImport(Dll)] public static extern int pawnio_close(IntPtr handle);
        [DllImport(Dll, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern int pawnio_execute(IntPtr handle, string function, ulong[] input, UIntPtr inputCount,
            ulong[] output, UIntPtr outputCount, out UIntPtr returnedCount);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string path);

        public const int E_ACCESSDENIED = unchecked((int)0x80070005);

        static bool _ready;

        /// <summary>Carrega o PawnIOLib.dll (ao lado do exe ou da instalação do PawnIO).</summary>
        public static void Load()
        {
            if (_ready) return;
            string here = Path.GetDirectoryName(Application.ExecutablePath);
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
            foreach (string dir in new[] { here, installed })
            {
                string dll = Path.Combine(dir, Dll);
                if (!File.Exists(dll) || LoadLibrary(dll) == IntPtr.Zero) continue;
                uint version;
                if (pawnio_version(out version) == 0) { _ready = true; return; }
            }
            throw new PawnIoException("O driver PawnIO não está instalado. Instale-o em https://pawnio.eu e tente de novo.");
        }
    }

    /// <summary>Um módulo PawnIO carregado no driver (uma instância por barramento).</summary>
    sealed class PawnIoModule : IDisposable
    {
        IntPtr _handle;

        PawnIoModule(IntPtr handle) { _handle = handle; }

        /// <summary>Carrega o módulo. Retorna null se ele não serve para este chipset.</summary>
        public static PawnIoModule TryLoad(string fileName)
        {
            string path = Locate(fileName);
            if (path == null) return null;
            byte[] blob = File.ReadAllBytes(path);

            IntPtr handle;
            int hr = PawnIoNative.pawnio_open(out handle);
            if (hr == PawnIoNative.E_ACCESSDENIED)
                throw new PawnIoException("Sem permissão para abrir o PawnIO. Execute o ColorShift como administrador.");
            if (hr != 0)
                throw new PawnIoException("Não foi possível abrir o driver PawnIO (0x" + hr.ToString("X8") + ").");

            if (PawnIoNative.pawnio_load(handle, blob, (UIntPtr)(uint)blob.Length) != 0)
            {
                PawnIoNative.pawnio_close(handle); // o módulo recusou o chipset (é de outro fabricante)
                return null;
            }
            return new PawnIoModule(handle);
        }

        static string Locate(string fileName)
        {
            string here = Path.GetDirectoryName(Application.ExecutablePath);
            string[] places = { Path.Combine(here, fileName), Path.Combine(here, "pawnio", fileName) };
            foreach (string p in places) if (File.Exists(p)) return p;
            return null;
        }

        /// <summary>Executa uma função exportada pelo módulo. Retorna o HRESULT.</summary>
        public int Call(string function, ulong[] input, ulong[] output)
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException("PawnIoModule");
            if (output == null || output.Length == 0) output = new ulong[1];
            UIntPtr returned;
            return PawnIoNative.pawnio_execute(_handle, function, input, (UIntPtr)(uint)input.Length,
                output, (UIntPtr)(uint)output.Length, out returned);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) PawnIoNative.pawnio_close(_handle);
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Um barramento SMBus do chipset (Intel i801 ou uma porta do AMD PIIX4), com as operações SMBus que os
    /// pentes de memória usam.
    /// </summary>
    sealed class PawnIoSmbus : IDisposable
    {
        // Parâmetros de ioctl_smbus_xfer: endereço, direção, comando, tipo de transação e 40 bytes de dados.
        enum Direction : ulong { Write = 0, Read = 1 }
        enum Transaction : ulong { Byte = 1, ByteData = 2, WordData = 3, BlockData = 5 }

        const int DataWords = 5;          // 5 × 8 = 40 bytes de área de dados
        const int MaxBlockLength = 32;    // limite do SMBus para um bloco

        // Mutex com nome compartilhado entre os programas que usam o SMBus no Windows (monitores de hardware,
        // utilitários de RGB). Segurá-lo durante a transação evita que dois programas se misturem no barramento.
        const string BusMutexName = "Global\\Access_SMBUS.HTP.Method";

        readonly PawnIoModule _module;
        readonly Mutex _busMutex;
        readonly object _sync = new object();

        public string Name { get; private set; }

        PawnIoSmbus(PawnIoModule module, string name)
        {
            _module = module;
            Name = name;
            // Deixa o módulo esperar dormindo em vez de girar a CPU (nem toda versão tem essa função).
            module.Call("ioctl_set_sleep_mode", new ulong[] { 2 }, null);
            try { _busMutex = new Mutex(false, BusMutexName); }
            catch (UnauthorizedAccessException) { _busMutex = null; }
        }

        /// <summary>Abre todos os barramentos SMBus que existirem nesta placa.</summary>
        public static List<PawnIoSmbus> OpenDramBuses()
        {
            PawnIoNative.Load();
            var result = new List<PawnIoSmbus>();

            PawnIoModule intel = PawnIoModule.TryLoad("SmbusI801.bin");
            if (intel != null) result.Add(new PawnIoSmbus(intel, "i801"));

            // O PIIX4 (AMD) tem duas portas; cada uma precisa da sua instância do módulo.
            for (ulong port = 0; port <= 1; port++)
            {
                PawnIoModule amd = PawnIoModule.TryLoad("SmbusPIIX4.bin");
                if (amd == null) continue;
                if (amd.Call("ioctl_piix4_port_sel", new[] { port }, null) == 0)
                    result.Add(new PawnIoSmbus(amd, "piix4_" + port));
                else
                    amd.Dispose();
            }
            return result;
        }

        /// <summary>Faz uma transação. <paramref name="data"/> entra e sai pela mesma área de 40 bytes.</summary>
        bool Xfer(int address, Direction direction, int command, Transaction kind, byte[] data)
        {
            var request = new ulong[4 + DataWords];
            request[0] = (ulong)address;
            request[1] = (ulong)direction;
            request[2] = (ulong)command;
            request[3] = (ulong)kind;
            Buffer.BlockCopy(data, 0, request, 4 * sizeof(ulong), Math.Min(data.Length, DataWords * sizeof(ulong)));
            var reply = new ulong[DataWords];

            int hr;
            lock (_sync)
            {
                bool held = AcquireBus();
                try { hr = _module.Call("ioctl_smbus_xfer", request, reply); }
                finally { if (held) _busMutex.ReleaseMutex(); }
            }
            if (hr != 0) return false;
            Buffer.BlockCopy(reply, 0, data, 0, Math.Min(data.Length, DataWords * sizeof(ulong)));
            return true;
        }

        bool AcquireBus()
        {
            if (_busMutex == null) return false;
            try
            {
                if (_busMutex.WaitOne(1000)) return true;
            }
            catch (AbandonedMutexException) { return true; }
            throw new PawnIoException("O SMBus está ocupado por outro programa.");
        }

        static byte[] NewBuffer() { return new byte[DataWords * sizeof(ulong)]; }

        /// <summary>Lê um byte sem comando (serve para ver se há algo no endereço). -1 se ninguém respondeu.</summary>
        public int ReadByte(int address)
        {
            byte[] d = NewBuffer();
            return Xfer(address, Direction.Read, 0, Transaction.Byte, d) ? d[0] : -1;
        }

        public int ReadByteData(int address, int command)
        {
            byte[] d = NewBuffer();
            return Xfer(address, Direction.Read, command, Transaction.ByteData, d) ? d[0] : -1;
        }

        public bool WriteByteData(int address, int command, byte value)
        {
            byte[] d = NewBuffer();
            d[0] = value;
            return Xfer(address, Direction.Write, command, Transaction.ByteData, d);
        }

        /// <summary>Escreve uma palavra; o SMBus manda o byte baixo primeiro.</summary>
        public bool WriteWordData(int address, int command, ushort value)
        {
            byte[] d = NewBuffer();
            d[0] = (byte)(value & 0xFF);
            d[1] = (byte)(value >> 8);
            return Xfer(address, Direction.Write, command, Transaction.WordData, d);
        }

        /// <summary>Escreve um bloco: o primeiro byte da área é o tamanho, seguido dos dados.</summary>
        public bool WriteBlockData(int address, int command, byte[] values, int offset, int length)
        {
            if (length > MaxBlockLength) length = MaxBlockLength;
            byte[] d = NewBuffer();
            d[0] = (byte)length;
            Array.Copy(values, offset, d, 1, length);
            return Xfer(address, Direction.Write, command, Transaction.BlockData, d);
        }

        public override string ToString() { return "SMBus " + Name; }

        public void Dispose()
        {
            lock (_sync) _module.Dispose();
            if (_busMutex != null) _busMutex.Dispose();
        }
    }

    sealed class PawnIoException : Exception
    {
        public PawnIoException(string message) : base(message) { }
    }
}
