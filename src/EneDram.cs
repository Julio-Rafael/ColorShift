// Controle dos LEDs de memória com controlador ENE (ASUS Aura DRAM, usado por vários fabricantes), portado de
// Controllers/ENESMBusController do OpenRGB (SPDX-License-Identifier: GPL-2.0-or-later, Adam Honse e colaboradores).
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace ColorShift
{
    /// <summary>Um efeito do controlador ENE.</summary>
    sealed class EneMode
    {
        public string Name;
        public byte Value;
        public bool HasColor;
        public bool HasSpeed;

        public EneMode(string name, byte value, bool color, bool speed)
        {
            Name = name;
            Value = value;
            HasColor = color;
            HasSpeed = speed;
        }
    }

    /// <summary>Um pente de memória com controlador ENE, em um endereço do SMBus.</summary>
    sealed class EneDramDevice
    {
        const int RegDeviceName = 0x1000;
        const int RegMicronCheck = 0x1030;
        const int RegConfigTable = 0x1C00;
        const int RegColorsEffect = 0x8010;
        const int RegDirect = 0x8020;
        const int RegMode = 0x8021;
        const int RegSpeed = 0x8022;
        const int RegDirection = 0x8023;
        const int RegApply = 0x80A0;
        const int RegSlotIndex = 0x80F8;
        const int RegI2cAddress = 0x80F9;
        const int RegColorsEffectV2 = 0x8160;

        const byte ApplyVal = 0x01;
        const byte SaveVal = 0xAA;
        const int ConfigChannelV1 = 0x13;
        const byte ChannelDram3 = 0x0E;
        const int MaxBlock = 3;

        // Velocidade do ENE: 4 = mais lenta, 0 = mais rápida.
        const int SpeedSlowest = 4;
        const int SpeedFastest = 0;

        // Endereços para onde os pentes são remapeados (todos começam em 0x77 depois de ligar o PC).
        static readonly byte[] RamAddresses = { 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x4F, 0x66, 0x67, 0x39, 0x3A, 0x3B, 0x3C, 0x3D };

        // Versões conhecidas do firmware: registrador de cor (v2 = 0x8160) e posição da contagem de LEDs na tabela.
        sealed class Layout
        {
            public bool ColorsV2;
            public int LedCountOffset;
            public bool CheckMode14;
            public Layout(bool v2, int countOffset, bool mode14 = false) { ColorsV2 = v2; LedCountOffset = countOffset; CheckMode14 = mode14; }
        }

        static readonly Dictionary<string, Layout> Layouts = new Dictionary<string, Layout>
        {
            { "LED-0116",        new Layout(false, 0x02) },
            { "DIMM_LED-0102",   new Layout(false, 0x02) },
            { "DIMM_LED-0103",   new Layout(false, 0x02) },
            { "AUMA0-E8K4-0101", new Layout(false, 0x02) },
            { "AUDA0-E6K5-0101", new Layout(true,  0x02, true) },
            { "AUMA0-E6K5-0104", new Layout(true,  0x02) },
            { "AUMA0-E6K5-0105", new Layout(true,  0x02) },
            { "AUMA0-E6K5-0106", new Layout(true,  0x02) },
            { "AUMA0-E6K5-0107", new Layout(true,  0x03) },
            { "AUMA0-E6K5-0008", new Layout(true,  0x03) },
            { "AUMA0-E6K5-1107", new Layout(true,  0x03) },
            { "AUMA0-E6K5-1110", new Layout(true,  0x03) },
            { "AUMA0-E6K5-1111", new Layout(true,  0x03) },
            { "AUMA0-E6K5-1113", new Layout(true,  0x03) },
            { "AUMA0-E6K5-1114", new Layout(true,  0x03) },
        };

        readonly PawnIoSmbus _bus;
        readonly int _effectReg;

        public int Address { get; private set; }
        public string Version { get; private set; }
        public int LedCount { get; private set; }
        public readonly List<EneMode> Modes = new List<EneMode>();

        EneDramDevice(PawnIoSmbus bus, int address)
        {
            _bus = bus;
            Address = address;

            var name = new byte[16];
            for (int i = 0; i < 16; i++) name[i] = ReadRegister(RegDeviceName + i);
            Version = CString(name, 15);

            var config = new byte[64];
            for (int i = 0; i < config.Length; i++) config[i] = ReadRegister(RegConfigTable + i);

            Layout layout;
            if (!Layouts.TryGetValue(Version, out layout))
            {
                LedCount = 0; // firmware desconhecido: o OpenRGB também ignora
                return;
            }
            _effectReg = layout.ColorsV2 ? RegColorsEffectV2 : RegColorsEffect;
            LedCount = config[layout.LedCountOffset];

            bool mode14 = false;
            if (layout.CheckMode14)
                for (int zone = 0; zone < 8; zone++)
                    if (config[ConfigChannelV1 + zone] == ChannelDram3) mode14 = true;

            // Mesmos efeitos do OpenRGB (sem o "Direct", que exige reenviar as cores o tempo todo).
            Modes.Add(new EneMode("Off", 0, false, false));
            Modes.Add(new EneMode("Static", 1, true, false));
            Modes.Add(new EneMode("Breathing", 2, true, true));
            Modes.Add(new EneMode("Flashing", 3, true, true));
            Modes.Add(new EneMode("Spectrum Cycle", 4, false, true));
            Modes.Add(new EneMode("Rainbow", 5, false, true));
            Modes.Add(new EneMode("Chase Fade", 7, true, true));
            Modes.Add(new EneMode("Chase", 9, true, true));
            Modes.Add(new EneMode("Random Flicker", 13, false, true));
            if (mode14) Modes.Add(new EneMode("Double Fade", 14, false, true));
        }

        public string Location { get { return _bus + ", endereço 0x" + Address.ToString("X2"); } }

        public override string ToString()
        {
            return "ENE DRAM " + Version + "  (" + Location + ")";
        }

        static string CString(byte[] bytes, int max)
        {
            int length = 0;
            while (length < max && bytes[length] != 0) length++;
            return Encoding.ASCII.GetString(bytes, 0, length);
        }

        /// <summary>
        /// Procura os pentes no barramento. Depois de ligar o PC todos respondem em 0x77; cada um é movido para um
        /// endereço livre da lista, um por vez (o pente que muda some de 0x77 e o próximo passa a responder).
        /// </summary>
        public static List<EneDramDevice> Detect(PawnIoSmbus bus)
        {
            int index = -1;
            for (int slot = 0; slot < 8; slot++)
            {
                if (bus.ReadByte(0x77) < 0) break;

                int res;
                do
                {
                    index++;
                    if (index >= RamAddresses.Length) break;
                    res = bus.ReadByte(RamAddresses[index]);
                } while (res >= 0);

                if (index >= RamAddresses.Length) break;
                WriteRegister(bus, 0x77, RegSlotIndex, (byte)slot);
                WriteRegister(bus, 0x77, RegI2cAddress, (byte)(RamAddresses[index] << 1));
            }

            var found = new List<EneDramDevice>();
            foreach (byte address in RamAddresses)
            {
                if (IsEneController(bus, address))
                {
                    var device = new EneDramDevice(bus, address);
                    if (device.LedCount > 0) found.Add(device);
                }
                Thread.Sleep(1);
            }
            return found;
        }

        /// <summary>Um ENE responde 0..15 nos registradores 0xA0..0xAF. Pentes Micron com a mesma assinatura são ignorados.</summary>
        static bool IsEneController(PawnIoSmbus bus, int address)
        {
            int res = bus.ReadByte(address);
            if (res < 0) res = bus.ReadByteData(address, 0x00);
            if (res < 0) return false;

            for (int i = 0xA0; i < 0xB0; i++)
                if (bus.ReadByteData(address, i) != i - 0xA0) return false;

            var check = new byte[16];
            for (int i = 0; i < 16; i++) check[i] = ReadRegister(bus, address, RegMicronCheck + i);
            return CString(check, 16) != "Micron";
        }

        // O ENE usa registradores de 16 bits: escreve o número (big-endian) no comando 0x00 e depois lê em 0x81
        // ou escreve em 0x01 (um byte) / 0x03 (bloco).
        static void SelectRegister(PawnIoSmbus bus, int address, int reg)
        {
            bus.WriteWordData(address, 0x00, (ushort)(((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF)));
        }

        static byte ReadRegister(PawnIoSmbus bus, int address, int reg)
        {
            SelectRegister(bus, address, reg);
            return (byte)bus.ReadByteData(address, 0x81);
        }

        static void WriteRegister(PawnIoSmbus bus, int address, int reg, byte value)
        {
            SelectRegister(bus, address, reg);
            bus.WriteByteData(address, 0x01, value);
        }

        byte ReadRegister(int reg) { return ReadRegister(_bus, Address, reg); }
        void WriteRegister(int reg, byte value) { WriteRegister(_bus, Address, reg, value); }

        void WriteRegisterBlock(int reg, byte[] data, int offset, int length)
        {
            SelectRegister(_bus, Address, reg);
            if (!_bus.WriteBlockData(Address, 0x03, data, offset, length))
                for (int i = 0; i < length; i++) _bus.WriteByteData(Address, 0x01, data[offset + i]);
        }

        /// <summary>Aplica o efeito com a cor 0xRRGGBB e a velocidade (0–100%).</summary>
        public void Apply(EneMode mode, int rgb, int speedPercent)
        {
            int speed = 0;
            if (mode.HasSpeed)
                speed = (int)Math.Round(SpeedSlowest + (SpeedFastest - SpeedSlowest) * Math.Max(0, Math.Min(100, speedPercent)) / 100.0);

            WriteRegister(RegMode, mode.Value);
            WriteRegister(RegSpeed, (byte)speed);
            WriteRegister(RegDirection, 0);
            WriteRegister(RegApply, ApplyVal);
            WriteRegister(RegDirect, 0);
            WriteRegister(RegApply, ApplyVal);

            if (mode.HasColor) SetAllColorsEffect(rgb);
        }

        void SetAllColorsEffect(int rgb)
        {
            // Ordem dos bytes no ENE: vermelho, azul, verde.
            var buffer = new byte[LedCount * 3];
            for (int i = 0; i < LedCount; i++)
            {
                buffer[i * 3] = (byte)(rgb >> 16);
                buffer[i * 3 + 1] = (byte)rgb;
                buffer[i * 3 + 2] = (byte)(rgb >> 8);
            }
            for (int sent = 0; sent < buffer.Length; sent += MaxBlock)
                WriteRegisterBlock(_effectReg + sent, buffer, sent, Math.Min(MaxBlock, buffer.Length - sent));
            WriteRegister(RegApply, ApplyVal);
        }

        /// <summary>Grava o efeito atual na memória do pente, para ele voltar assim depois de desligar o PC.</summary>
        public void SaveToDevice()
        {
            WriteRegister(RegApply, SaveVal);
        }
    }
}
