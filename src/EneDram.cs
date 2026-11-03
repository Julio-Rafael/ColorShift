// LEDs de memória com controlador ENE (o chip do ASUS Aura DRAM, usado por vários fabricantes de pentes).
//
// Como o chip funciona:
//  * Ele expõe um espaço de registradores de 16 bits atrás de um endereço SMBus. Para acessar um registrador,
//    escreve-se o número dele (com os bytes trocados) no comando 0x00; depois lê-se o valor no comando 0x81,
//    ou escreve-se um byte no comando 0x01 ou um bloco no comando 0x03.
//  * Ao ligar o PC todos os pentes respondem no endereço 0x77. Cada pente recebe um número de slot e um endereço
//    novo; quando muda, sai de 0x77 e o próximo pente passa a responder ali.
//  * 0x1000: nome/versão do firmware (texto); 0x1C00: tabela de configuração (contagem de LEDs, canais).
//  * 0x8020 modo direto, 0x8021 efeito, 0x8022 velocidade, 0x8023 direção, 0x80A0 aplicar (0x01) ou gravar (0xAA).
//  * As cores do efeito ficam em 0x8010 (firmwares antigos) ou 0x8160 (novos), 3 bytes por LED na ordem R, B, G.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace ColorShift
{
    /// <summary>Um efeito do controlador ENE.</summary>
    sealed class EneMode
    {
        public readonly string Name;
        public readonly byte Value;
        public readonly bool HasColor;
        public readonly bool HasSpeed;

        public EneMode(string name, byte value, bool color, bool speed)
        {
            Name = name;
            Value = value;
            HasColor = color;
            HasSpeed = speed;
        }
    }

    /// <summary>Acesso aos registradores de 16 bits de um chip ENE em um endereço do SMBus.</summary>
    struct EneRegisters
    {
        readonly PawnIoSmbus _bus;
        readonly int _address;

        public EneRegisters(PawnIoSmbus bus, int address) { _bus = bus; _address = address; }

        void Point(int register)
        {
            // O chip espera o número do registrador em big-endian; WriteWordData manda o byte baixo primeiro.
            ushort swapped = (ushort)(((register & 0xFF) << 8) | ((register >> 8) & 0xFF));
            _bus.WriteWordData(_address, 0x00, swapped);
        }

        public byte Read(int register)
        {
            Point(register);
            return (byte)_bus.ReadByteData(_address, 0x81);
        }

        public byte[] Read(int register, int count)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = Read(register + i);
            return bytes;
        }

        public void Write(int register, byte value)
        {
            Point(register);
            _bus.WriteByteData(_address, 0x01, value);
        }

        /// <summary>Escreve vários bytes seguidos. Se o barramento não aceitar bloco, manda byte a byte.</summary>
        public void Write(int register, byte[] values, int offset, int count)
        {
            Point(register);
            if (_bus.WriteBlockData(_address, 0x03, values, offset, count)) return;
            for (int i = 0; i < count; i++) _bus.WriteByteData(_address, 0x01, values[offset + i]);
        }
    }

    /// <summary>Um pente de memória com controlador ENE.</summary>
    sealed class EneDramDevice
    {
        static class Reg
        {
            public const int FirmwareName = 0x1000;
            public const int VendorName = 0x1030;
            public const int ConfigTable = 0x1C00;
            public const int EffectColorsOld = 0x8010;
            public const int DirectMode = 0x8020;
            public const int Effect = 0x8021;
            public const int Speed = 0x8022;
            public const int Direction = 0x8023;
            public const int Commit = 0x80A0;
            public const int SlotIndex = 0x80F8;
            public const int BusAddress = 0x80F9;
            public const int EffectColorsNew = 0x8160;
        }

        const byte CommitApply = 0x01;
        const byte CommitSave = 0xAA;
        const int PowerOnAddress = 0x77;
        const int ColorChunk = 3; // o chip só aceita blocos de até 3 bytes nas cores

        // Endereços livres para onde os pentes são movidos.
        static readonly int[] SpareAddresses = { 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D };

        /// <summary>O que muda entre as versões de firmware conhecidas.</summary>
        sealed class Firmware
        {
            public readonly bool NewColorRegister;   // cores em 0x8160 em vez de 0x8010
            public readonly int LedCountIndex;       // posição da contagem de LEDs na tabela de configuração
            public readonly bool MayHaveDoubleFade;  // efeito 14 existe se algum canal for do tipo DRAM 3

            public Firmware(bool newColors, int ledCountIndex, bool doubleFade = false)
            {
                NewColorRegister = newColors;
                LedCountIndex = ledCountIndex;
                MayHaveDoubleFade = doubleFade;
            }
        }

        static readonly Dictionary<string, Firmware> KnownFirmware = BuildFirmwareTable();

        static Dictionary<string, Firmware> BuildFirmwareTable()
        {
            var table = new Dictionary<string, Firmware>();
            foreach (string v in new[] { "LED-0116", "DIMM_LED-0102", "DIMM_LED-0103", "AUMA0-E8K4-0101" })
                table[v] = new Firmware(false, 2);
            table["AUDA0-E6K5-0101"] = new Firmware(true, 2, true);
            foreach (string v in new[] { "0104", "0105", "0106" })
                table["AUMA0-E6K5-" + v] = new Firmware(true, 2);
            foreach (string v in new[] { "0107", "0008", "1107", "1110", "1111", "1113", "1114" })
                table["AUMA0-E6K5-" + v] = new Firmware(true, 3);
            return table;
        }

        // Efeitos oferecidos: (nome, número no chip, usa cor, usa velocidade).
        static readonly EneMode[] StandardModes =
        {
            new EneMode("Off", 0, false, false),
            new EneMode("Static", 1, true, false),
            new EneMode("Breathing", 2, true, true),
            new EneMode("Flashing", 3, true, true),
            new EneMode("Spectrum Cycle", 4, false, true),
            new EneMode("Rainbow", 5, false, true),
            new EneMode("Chase Fade", 7, true, true),
            new EneMode("Chase", 9, true, true),
            new EneMode("Random Flicker", 13, false, true),
        };
        static readonly EneMode DoubleFade = new EneMode("Double Fade", 14, false, true);

        const int ChannelTableIndex = 0x13;
        const byte ChannelTypeDram3 = 0x0E;

        readonly EneRegisters _regs;
        readonly PawnIoSmbus _bus;
        readonly int _colorRegister;

        public int Address { get; private set; }
        public string Version { get; private set; }
        public int LedCount { get; private set; }
        public readonly List<EneMode> Modes = new List<EneMode>();

        EneDramDevice(PawnIoSmbus bus, int address)
        {
            _bus = bus;
            _regs = new EneRegisters(bus, address);
            Address = address;
            Version = AsciiZ(_regs.Read(Reg.FirmwareName, 16), 15);

            Firmware firmware;
            if (!KnownFirmware.TryGetValue(Version, out firmware)) return; // versão desconhecida: não mexe

            byte[] config = _regs.Read(Reg.ConfigTable, 64);
            LedCount = config[firmware.LedCountIndex];
            _colorRegister = firmware.NewColorRegister ? Reg.EffectColorsNew : Reg.EffectColorsOld;

            Modes.AddRange(StandardModes);
            if (firmware.MayHaveDoubleFade &&
                config.Skip(ChannelTableIndex).Take(8).Any(channel => channel == ChannelTypeDram3))
                Modes.Add(DoubleFade);
        }

        public string Location { get { return _bus + ", endereço 0x" + Address.ToString("X2"); } }

        public override string ToString() { return "ENE DRAM " + Version + "  (" + Location + ")"; }

        static string AsciiZ(byte[] bytes, int max)
        {
            int end = Array.IndexOf(bytes, (byte)0, 0, Math.Min(max, bytes.Length));
            if (end < 0) end = Math.Min(max, bytes.Length);
            return Encoding.ASCII.GetString(bytes, 0, end);
        }

        /// <summary>Dá um endereço próprio a cada pente que ainda está em 0x77 e devolve os pentes ENE encontrados.</summary>
        public static List<EneDramDevice> Detect(PawnIoSmbus bus)
        {
            AssignAddresses(bus);

            var devices = new List<EneDramDevice>();
            foreach (int address in SpareAddresses)
            {
                if (LooksLikeEne(bus, address))
                {
                    var device = new EneDramDevice(bus, address);
                    if (device.LedCount > 0) devices.Add(device);
                }
                Thread.Sleep(1);
            }
            return devices;
        }

        static void AssignAddresses(PawnIoSmbus bus)
        {
            var free = new Queue<int>(SpareAddresses);
            var waiting = new EneRegisters(bus, PowerOnAddress);

            for (byte slot = 0; slot < 8 && bus.ReadByte(PowerOnAddress) >= 0; slot++)
            {
                // Próximo endereço da lista em que ninguém responde.
                int target = -1;
                while (free.Count > 0)
                {
                    int candidate = free.Dequeue();
                    if (bus.ReadByte(candidate) < 0) { target = candidate; break; }
                }
                if (target < 0) return;

                waiting.Write(Reg.SlotIndex, slot);
                waiting.Write(Reg.BusAddress, (byte)(target << 1)); // o chip recebe o endereço de 8 bits
            }
        }

        /// <summary>
        /// Os registradores 0xA0..0xAF de um ENE devolvem 0..15. Alguns pentes Micron têm a mesma assinatura
        /// mas não são ENE; eles se identificam como "Micron" em 0x1030.
        /// </summary>
        static bool LooksLikeEne(PawnIoSmbus bus, int address)
        {
            if (bus.ReadByte(address) < 0 && bus.ReadByteData(address, 0x00) < 0) return false;

            for (int n = 0; n < 16; n++)
                if (bus.ReadByteData(address, 0xA0 + n) != n) return false;

            string vendor = AsciiZ(new EneRegisters(bus, address).Read(Reg.VendorName, 16), 16);
            return vendor != "Micron";
        }

        /// <summary>Aplica o efeito com a cor 0xRRGGBB e a velocidade (0–100%).</summary>
        public void Apply(EneMode mode, int rgb, int speedPercent)
        {
            // No chip a velocidade vai de 4 (mais lenta) a 0 (mais rápida).
            byte speed = 0;
            if (mode.HasSpeed)
            {
                int percent = Math.Max(0, Math.Min(100, speedPercent));
                speed = (byte)Math.Round(4 - 4 * percent / 100.0);
            }

            _regs.Write(Reg.Effect, mode.Value);
            _regs.Write(Reg.Speed, speed);
            _regs.Write(Reg.Direction, 0);
            _regs.Write(Reg.Commit, CommitApply);
            _regs.Write(Reg.DirectMode, 0); // usa o efeito interno, não cores enviadas pelo PC
            _regs.Write(Reg.Commit, CommitApply);

            if (mode.HasColor) WriteEffectColor(rgb);
        }

        void WriteEffectColor(int rgb)
        {
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            var colors = new byte[LedCount * 3];
            for (int led = 0; led < LedCount; led++)
            {
                colors[led * 3] = r;
                colors[led * 3 + 1] = b;
                colors[led * 3 + 2] = g;
            }

            for (int pos = 0; pos < colors.Length; pos += ColorChunk)
                _regs.Write(_colorRegister + pos, colors, pos, Math.Min(ColorChunk, colors.Length - pos));
            _regs.Write(Reg.Commit, CommitApply);
        }

        /// <summary>Grava o efeito atual na memória do pente, para ele voltar assim depois de desligar o PC.</summary>
        public void SaveToDevice()
        {
            _regs.Write(Reg.Commit, CommitSave);
        }
    }
}
