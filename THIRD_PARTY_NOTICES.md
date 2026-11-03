# Avisos de terceiros

## PawnIO.Modules: SmbusI801.bin e SmbusPIIX4.bin

- Arquivos: `pawnio/SmbusI801.bin` e `pawnio/SmbusPIIX4.bin` (também vão no `ColorShift.zip` das Releases)
- Origem: https://github.com/namazso/PawnIO.Modules (módulos oficiais do PawnIO, assinados por namazso)
- Copyright (C) 2025 Steve-Tech <me@stevetech.au>
- Licença: GNU Lesser General Public License v2.1 ou posterior (`LGPL-2.1-or-later`). O texto completo está em
  [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt).
- Os módulos são distribuídos **sem modificação**, como binários assinados. O código-fonte correspondente
  (`SmbusI801.p`, `SmbusPIIX4.p` e os arquivos incluídos) está disponível em
  https://github.com/namazso/PawnIO.Modules.

O ColorShift não inclui código desses módulos: ele só os carrega no driver PawnIO e chama as funções que eles
exportam. O driver PawnIO e o `PawnIOLib.dll` não vêm com o ColorShift; são instalados à parte, a partir de
https://pawnio.eu.

Estes módulos são distribuídos SEM NENHUMA GARANTIA, conforme a licença acima.
