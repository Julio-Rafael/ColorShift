# ColorShift

Aumenta o **Digital Vibrance** (NVIDIA) ou a **saturação** (AMD) automaticamente quando um jogo está em foco
e volta ao normal na área de trabalho. É uma reimplementação própria do vibranceGUI, feita a partir da engenharia
reversa documentada em [docs/ANALISE.md](docs/ANALISE.md).

## Abas

- **Saturação** — vibrance/saturação pela placa de vídeo, por jogo (o que já existia).
- **Monitor** — brilho, contraste, nitidez, predefinição de cor, ganho RGB e padrão de fábrica, gravados no próprio
  monitor por DDC/CI (mesmo canal do menu do monitor). Só aparecem os controles que o monitor suporta.
- **Mira** — mira na tela (cruz, ponto, círculo, T), com cor, tamanho, espessura, espaço, contorno e deslocamento.
  Pode aparecer só quando um jogo com perfil está em foco. Também liga/desliga pelo menu da bandeja.
- **Resolução e FPS** — troca resolução e taxa de atualização (Hz); volta sozinho em 15 s se não confirmar.
- **Memória RAM** — efeito, cor, velocidade e brilho dos LEDs dos pentes com controlador **ENE** (ASUS Aura DRAM),
  direto pelo SMBus, **sem o OpenRGB**. Precisa só do driver [PawnIO](https://pawnio.eu) instalado e do ColorShift
  rodando como administrador (a aba tem o botão **Reiniciar como administrador**). **Gravar no pente…** salva o efeito
  na memória do próprio pente, para ele ficar assim mesmo com o ColorShift fechado. Feche o OpenRGB antes de usar.

Tema **Sistema / Claro / Escuro**: seletor no topo da aba Saturação. "Sistema" segue o modo escuro do Windows.

## Download

Baixe o `ColorShift.zip` da página [Releases](https://github.com/Julio-Rafael/ColorShift/releases/latest),
extraia numa pasta e abra o `ColorShift.exe`. Precisa do .NET Framework 4.x (já vem no Windows 10 e 11).
Para a aba **Memória RAM**, instale também o driver [PawnIO](https://pawnio.eu).

## Como usar

1. Abra o `ColorShift.exe` (ou `bin\ColorShift.exe`, se você compilou).
2. Clique em **Importar do vibranceGUI** para trazer seus perfis atuais (ex.: VALORANT), ou adicione jogos com
   **Adicionar .exe…** / **Processos abertos…**.
3. Selecione um perfil e ajuste o nível dele. O controle de cima define o nível da área de trabalho.
4. Fechar a janela deixa o programa rodando na bandeja. Para sair, clique com o botão direito no ícone → **Sair**
   (os valores originais dos monitores são restaurados).

Escala: **50% = padrão do driver**, 100% = máximo. Na NVIDIA não dá para ficar abaixo de 50%.

As configurações ficam em `%APPDATA%\ColorShift\settings.xml`. A inicialização com o Windows usa
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

> Feche o vibranceGUI antes de usar o ColorShift: os dois brigariam pelo mesmo ajuste.

## Compilar

Execute `build.bat`. Ele usa o `csc.exe` do .NET Framework 4.x, que já vem no Windows, e gera `bin\ColorShift.exe`.
Não é preciso instalar nenhum SDK.

## Estrutura

| Arquivo | Função |
|---|---|
| `src/NvidiaController.cs` | NvAPI via `nvapi_QueryInterface` (GetDVCInfo / SetDVCLevel) |
| `src/AmdController.cs` | ADL (`ADL_Display_Color_Get/Set`, saturação) |
| `src/ForegroundWatcher.cs` | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` |
| `src/VibranceEngine.cs` | Decide o nível de cada monitor |
| `src/Settings.cs` | Configuração, importação do vibranceGUI, inicialização automática |
| `src/MainForm.cs`, `src/ProcessPicker.cs` | Interface (WinForms) e bandeja |
| `src/DdcMonitor.cs`, `src/MonitorPage.cs` | DDC/CI (`dxva2.dll`): capacidades do monitor, leitura/escrita VCP |
| `src/Crosshair.cs` | Mira: configurações, desenho, janela overlay e aba |
| `src/Theme.cs` | Modo escuro: cores, barra de título escura, abas e cabeçalhos pintados pelo app |
| `src/DisplayPage.cs` | Resolução e Hz (`EnumDisplaySettings` / `ChangeDisplaySettingsEx`) |
| `src/PawnIoSmbus.cs` | SMBus do chipset (Intel i801 / AMD PIIX4) pelo driver PawnIO, com o mutex global de SMBus |
| `src/EneDram.cs` | Controlador ENE dos pentes: detecção, remapeamento de endereço, efeitos e cores |
| `src/RamPage.cs` | Aba de LEDs da memória |
| `pawnio/*.bin` | Módulos PawnIO assinados (de namazso/PawnIO.Modules), copiados para `bin` pelo `build.bat` |

| `docs/ANALISE.md` | Relatório da engenharia reversa com o REA |

O acesso ao SMBus e o controle dos LEDs são código próprio do ColorShift: usam só a API pública do PawnIO e as
funções que os módulos assinados exportam, mais o mapa de registradores do chip ENE.

## Licenças de terceiros

Os módulos `SmbusI801.bin` e `SmbusPIIX4.bin` são de [namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)
(autor Steve-Tech), sob **LGPL-2.1-or-later**. Eles são distribuídos sem modificação; o código-fonte está nesse
repositório. Veja [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) e [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt).
