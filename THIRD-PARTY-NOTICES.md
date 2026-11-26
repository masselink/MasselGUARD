# Third-Party Notices

MasselGUARD's own source code is licensed under the MIT License (see [`LICENSE`](LICENSE)).
MasselGUARD also **includes or depends on** the third-party software listed below.
Each remains under its **own** license; the notices below are provided to satisfy
those licenses' attribution requirements. The MIT License of MasselGUARD does not
apply to these components.

Upstream license information was last checked on 2026-11-26.

## WireGuard components (bundled)

**WireGuard** is a registered trademark of Jason A. Donenfeld. MasselGUARD is an
independent project and is **not** sponsored by, endorsed by, or affiliated with
WireGuard LLC or Jason A. Donenfeld.

Two native DLLs per architecture (x64 and ARM64) are stored in
`wireguard-deps/<arch>/` in the source repository and are included in the release
packages.

### `wireguard.dll` - WireGuardNT 1.1

- Upstream: <https://git.zx2c4.com/wireguard-nt>
- Copyright © 2018-2026 WireGuard LLC. All Rights Reserved.
- Origin: the unmodified prebuilt DLL from the official archive at
  <https://download.wireguard.com/wireguard-nt/>.
- License: **Prebuilt Binaries License** (not MIT, and not open source):
  <https://git.zx2c4.com/wireguard-nt/plain/prebuilt-binaries-license.txt>.
  The WireGuardNT source code is licensed separately, under GPLv2. The API header
  `wireguard.h` is licensed "GPL-2.0 OR MIT".

The following is a short summary. **The license text at the link above governs.**
Among other restrictions, it does not allow:

- reverse engineering or modifying the DLL (derivative work is allowed only
  insofar as it uses just the API in `wireguard.h`, the "Permitted API");
- removing proprietary notices, labels or copyrights;
- redistributing, selling or sublicensing the DLL without WireGuard LLC's prior
  written consent, **except** insofar as it is distributed alongside other
  software that uses it only through the Permitted API;
- using WireGuard LLC's, the WireGuard project's or its contributors' names to
  endorse or promote derived products without prior written consent.

MasselGUARD distributes the DLL alongside its own software. MasselGUARD's own code
calls it only through `WireGuardOpenAdapter` and `WireGuardCloseAdapter`, which
are part of the Permitted API. Do not distribute `wireguard.dll` on its own. The
DLL is provided "as is", without warranty. Anyone who forks or redistributes
MasselGUARD must comply with this license too.

### `tunnel.dll` - WireGuard embeddable tunnel service

- Upstream: <https://git.zx2c4.com/wireguard-windows> (`embeddable-dll-service`)
- Built from source by `tunnelbuild/`. The bundled DLLs were built from commit
  `6ece77bc487c8aa697e3c092197621c4f3e5ccb8` with Go 1.27.1. The build script
  changes where the upstream `build.bat` downloads the `wireguard-tools` source
  (the official GitHub mirror), so the build information marks the source tree
  as modified.
- Copyright (C) 2018-2026 WireGuard LLC. All Rights Reserved.
- License: **MIT** (text under [MIT License](#mit-license)).

The following are compiled into `tunnel.dll`. All are under the
[BSD 3-Clause License](#bsd-3-clause-license-go), Copyright 2009 The Go Authors:

| Component | Version |
|---|---|
| Go runtime and standard library | 1.27.1 |
| `golang.org/x/crypto` | v0.57.0 |
| `golang.org/x/sys` | v0.48.0 |
| `golang.org/x/text` | v0.42.1-0.20260918223412-6dcc5b674302 |

The versions in this section describe the DLLs currently in the repository. Update
them when the DLLs are rebuilt (`go version -m tunnel.dll` lists them).

## .NET libraries (NuGet packages)

| Package | Version | Copyright | License |
|---|---|---|---|
| [Markdig](https://github.com/xoofx/markdig) | 0.38.0 | Alexandre Mutel | [BSD 2-Clause](#bsd-2-clause-license-markdig) |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | 0.16.9 | Author: Michael Jahn | [Apache-2.0](LICENSE-Apache-2.0.txt) |
| [ZXing.Net.Bindings.Windows.Compatibility](https://github.com/micjahn/ZXing.Net) | 0.16.9 | Author: Michael Jahn | [Apache-2.0](LICENSE-Apache-2.0.txt) |
| System.ServiceProcess.ServiceController | 8.0.0 | © Microsoft Corporation | [MIT](#mit-license) |
| System.Security.Cryptography.ProtectedData | 8.0.0 | © Microsoft Corporation | [MIT](#mit-license) |
| System.Diagnostics.EventLog (indirect dependency) | 8.0.0 | © Microsoft Corporation | [MIT](#mit-license) |

ZXing.Net is a port of the Java barcode library
[ZXing](https://github.com/zxing/zxing).

## Microsoft .NET

MasselGUARD targets **.NET 10** and publishes **framework-dependent**: the .NET
runtime is **not** bundled (the end user supplies it). The executables contain the
.NET application host. Copyright (c) .NET Foundation and Contributors,
[MIT License](#mit-license). <https://github.com/dotnet/runtime>

---

## Fonts

Any bundled theme fonts (e.g. Orbitron, JetBrains Mono - SIL Open Font License)
live in the separate **MasselGUARD-themes** repository, not this one, and are
attributed there. If a font is ever shipped in this repository, add its OFL notice
here.

---

## License texts

### MIT License

Applies to `tunnel.dll` (Copyright (C) 2018-2026 WireGuard LLC. All Rights
Reserved.), to the .NET application host (Copyright (c) .NET Foundation and
Contributors) and to the Microsoft packages listed above (© Microsoft
Corporation).

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### BSD 3-Clause License (Go)

```
Copyright 2009 The Go Authors.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * Neither the name of Google LLC nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### BSD 2-Clause License (Markdig)

```
Copyright (c) Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
