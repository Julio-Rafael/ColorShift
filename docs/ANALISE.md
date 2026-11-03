# Engenharia reversa do vibranceGUI

Análise feita com o **REA** (`rea-agents@6.7.0`, de github.com/morluto/rea), só com análise estática:
nenhum código do vibranceGUI foi carregado nem executado durante a análise.

| Arquivo | SHA-256 | Tipo |
|---|---|---|
| `Downloads\vibranceGUI\vibranceGUI.exe` | `1cbaa4d4c817743a7ec88bdc3f8d15200e543a86e0b3374c6d05a15a0762970f` | .NET Framework, CIL, x86 (32 bits obrigatório) |
| `%APPDATA%\vibranceGUI\vibranceDLL.dll` | — | C++ nativo x86, extraído pelo app na primeira execução |

Comandos usados:

```
rea inspect-managed-artifact  vibranceGUI.exe --json
rea inspect-managed-members   vibranceGUI.exe --json   # tipos, métodos, chamadas, strings (ldstr)
rea inspect-binary-layout     vibranceDLL.dll --json
```

As evidências brutas (`members.json`) estão em `docs/evidencias/`.

## Arquitetura encontrada

```
vibrance.GUI.Program            -> mutex "vibranceGUI~Mutex", detecta a GPU, abre a janela
vibrance.GUI.common.GraphicsAdapterHelper
                                -> LoadLibrary("nvapi.dll") / LoadLibrary("atiadlxx.dll" | "atiadlxy.dll")
vibrance.GUI.common.IVibranceProxy
  ├─ NVIDIA.NvidiaDynamicVibranceProxy  -> P/Invoke em vibranceDLL.dll (wrapper C++ da NvAPI)
  └─ AMD.AmdDynamicVibranceProxy        -> AmdAdapter32/64 -> P/Invoke direto no ADL
vibrance.GUI.common.WinEventHook       -> SetWinEventHook (janela em primeiro plano)
vibrance.GUI.common.ResolutionHelper   -> ChangeDisplaySettingsEx (troca de resolução opcional)
vibrance.GUI.common.SettingsController -> %APPDATA%\vibranceGUI\vibranceGUI.ini + applicationData.xml
vibrance.GUI.common.RegistryController -> HKCU\...\CurrentVersion\Run  ("vibranceGUI" = "<exe>" -minimized)
Costura.AssemblyLoader                 -> DLLs .NET embutidas (Fody/Costura)
```

## Como o vibrance é aplicado (NVIDIA)

O `vibranceDLL.dll` exporta métodos C++ (`?setDVCLevel@vibrance@vibranceDLL@@QAE_NHH@Z` etc.) e resolve as
funções da NvAPI por `nvapi_QueryInterface(id)`. Os IDs abaixo foram encontrados no binário:

| Função NvAPI | ID | Offset no DLL |
|---|---|---|
| NvAPI_Initialize | `0x0150E828` | 0x429 |
| NvAPI_Unload | `0xD22BDD7E` | 0x222c |
| NvAPI_EnumPhysicalGPUs | `0xE5AC921F` | 0x223c |
| NvAPI_GPU_GetFullName | `0xCEEE8E9F` | 0x224c |
| NvAPI_GPU_GetActiveOutputs | `0xE3E89B6F` | 0x225c |
| NvAPI_GetDVCInfo | `0x4085DE45` | 0x226c |
| NvAPI_SetDVCLevel | `0x172409B4` | 0x227c |
| NvAPI_EnumNvidiaDisplayHandle | `0x9ABDD40D` | 0x228c |
| NvAPI_GetDVCInfoEx | `0x0E45002D` | 0x22bc |
| NvAPI_GetAssociatedNvidiaDisplayHandle | `0x35C29134` | 0x22cc |
| NvAPI_GPU_GetSystemType | `0xBAAABFCC` | 0x22dc |

Estrutura `NvDisplayDvcInfo` (`version`, `currentLevel`, `minLevel`, `maxLevel`). O nível DVC vai de 0 (padrão)
a 63. A interface mostra isso como 50%–100%: `NvidiaVibranceValueWrapper.GenerateSettingsWrapper` monta uma tabela
de 64 entradas com essa conversão.

## Fluxo em tempo de execução (NVIDIA)

Reconstruído a partir das chamadas e dos acessos a campos de cada método:

1. `.ctor` → `initializeLibrary` → `InitializeProxy`
   - `enumeratePhsyicalGPUs` + `getGpuSystemType` (aborta se for desconhecido),
   - `EnumerateDisplayHandles` (preenche `VibranceInfo.displayHandles`),
   - `getActiveOutputs`, `getGpuName`, `getDVCInfo`; se o nível atual ≠ `userVibranceSettingDefault`, chama `setDVCLevel`.
2. Registra `OnWinEventHook` no singleton `WinEventHook` (que observa a troca de janela em primeiro plano).
3. `OnWinEventHook(e)`:
   - procura em `_applicationSettings` um perfil com `Name == e.ProcessName` (lambda `<OnWinEventHook>b__0`);
   - **com perfil**: `GetApplicationDisplayHandle(hwnd)` → `Screen.FromHandle(hwnd).DeviceName` →
     `getAssociatedNvidiaDisplayHandle`; troca a resolução se for preciso; `setDVCLevel(handle, IngameLevel)`
     se `equalsDVCLevel` disser que o nível é diferente;
   - **sem perfil** (com `isWindowActive`): restaura a resolução; se `affectPrimaryMonitorOnly`, aplica
     `userVibranceSettingDefault` só em `defaultHandle`; senão, em todos os `displayHandles` (`TrueForAll` / `ForEach`).
4. `HandleDvcExit` ao fechar: coloca o nível de Windows em todos os monitores.

O caminho AMD é igual, mas usa `ADL_Display_Color_Set` com o tipo de cor saturação (4) através de
`AmdAdapter64.SetSaturationOnDisplay` / `SetSaturationOnAllDisplays`.

## Configuração (formato do vibranceGUI)

`vibranceGUI.ini`:

```
[Settings]
inactiveValue=0             ; nível DVC bruto fora dos jogos
affectPrimaryMonitorOnly=False
neverSwitchResolution=True
```

`applicationData.xml` → `ArrayOfApplicationSetting/ApplicationSetting` com `Name`, `FileName`,
`IngameLevel` (nível bruto), `IsResolutionChangeNeeded` e `ResolutionSettings` (DEVMODE).

## O que o ColorShift faz diferente

- **Sem DLL nativa**: chama a NvAPI (`nvapi64.dll`) e o ADL (`atiadlxx.dll`) direto do C#, em 64 bits.
- **Porcentagem unificada** (50% = padrão do driver), que vale tanto para NVIDIA quanto para AMD; perfis importados
  do vibranceGUI são convertidos.
- **Restaura os valores originais** dos monitores ao sair, em vez de forçar o nível de Windows.
- Encontra o jogo pelo **caminho completo**, com o nome do .exe como alternativa, usando
  `QueryFullProcessImageName` (funciona com jogos que têm anti-cheat, como o VALORANT).
- **Não inclui** a troca automática de resolução.
- O código é novo e próprio; nada do vibranceGUI foi copiado.

# Análise do monitor (LG E2342V)

O software da LG (OnScreen Control) não está instalado nesta máquina, e ele só fala com o monitor por **DDC/CI**
(VESA MCCS). Por isso a fonte de verdade foi o próprio monitor: a string de capacidades lida com
`CapabilitiesRequestAndCapabilitiesReply` (dxva2.dll), sem gravar nada.

```
prot(monitor)type(lcd)model(E2342V)cmds(01 02 03 0C E3 F3)
vcp(02030405080B0C101214(01 05 06 07 08 0B)1516181A4D4E4F5260(01 03 04)626C6E70878DACAEB6C0C6C8C9D6(01 04)
DFE0E1E3(00 01 02 03 04 10 11 12 13 14)ECEFFD(00 01)FE(00 01 02)F4F5F6F7F8F9FF)mswhql(1)mccs_ver(2.1)
```

| VCP | Função | No ColorShift |
|---|---|---|
| 04 / 08 | Restaurar padrão de fábrica / só cores | Aba Monitor → Padrão de fábrica |
| 10 / 12 / 87 | Brilho / Contraste / Nitidez | Aba Monitor → Ajuste de imagem |
| 14 | Predefinição de cor: sRGB, 6500 K, 7500 K, 8200 K, 9300 K, Usuário | Aba Monitor → Ajuste de cor |
| 16 / 18 / 1A | Ganho vermelho / verde / azul (vale no modo Usuário) | Aba Monitor → Ajuste de cor |
| 60 | Entrada (VGA, DVI, HDMI) | não exposto |
| E0–FF | Específicos da LG, sem documentação | não exposto (não gravamos códigos desconhecidos) |

O que o monitor **não** tem: **saturação** (VCP 8A ausente) e **mira** por hardware (recurso só dos modelos gamer
mais novos). A saturação continua vindo da GPU (aba Saturação) e a mira é uma janela overlay transparente para
cliques (`WS_EX_LAYERED | WS_EX_TRANSPARENT`), que não injeta nada no jogo. Resolução e Hz usam
`ChangeDisplaySettingsEx`, como o `ResolutionHelper` do vibranceGUI, com teste (`CDS_TEST`) antes e reversão
automática em 15 s.
