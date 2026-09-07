# Third-Party Notices

MasselGUARD's own source code is licensed under the MIT License (see [`LICENSE`](LICENSE)).
MasselGUARD also **includes or depends on** the third-party software listed below.
Each remains under its **own** license; the notices below are provided to satisfy
those licenses' attribution requirements.

## WireGuard components (bundled)

**WireGuard** is a registered trademark of Jason A. Donenfeld. MasselGUARD is an
independent project and is **not** sponsored by, endorsed by, or affiliated with
WireGuard LLC or Jason A. Donenfeld.

MasselGUARD ships two native DLLs per architecture in `wireguard-deps/<arch>/`:

### `tunnel.dll` — WireGuard embeddable tunnel service (userspace, Go)
- Upstream: <https://git.zx2c4.com/wireguard-windows> (built via its embeddable
  DLL service, which uses <https://git.zx2c4.com/wireguard-go>)
- Copyright © WireGuard LLC and contributors
- License: **MIT** — _verify against the wireguard-windows / wireguard-go
  `LICENSE` for the exact version bundled, and include its text._

### `wireguard.dll` — wireguard-nt (kernel driver, WireGuard-NT)
- Upstream: <https://git.zx2c4.com/wireguard-nt>
- Copyright © WireGuard LLC and contributors
- License: **VERIFY UPSTREAM.** wireguard-nt carries its own license/redistribution
  terms for the prebuilt DLL — confirm the exact license, confirm redistribution of
  the prebuilt `wireguard.dll` is permitted, and reproduce its required notice text
  here. **Do not assume.** If its terms are copyleft, review whether they affect the
  combined distribution with legal counsel.

## Microsoft .NET

MasselGUARD targets **.NET 10** and publishes **framework-dependent** — the .NET
runtime is **not** bundled (the end user supplies it). .NET is © Microsoft
Corporation, MIT licensed. <https://github.com/dotnet/runtime>

---

## Planned dependency (NOT yet bundled) — WinDivert

Reserved for future **per-app split tunneling** (a later 4.x). **Not present in
current releases** — this entry is a placeholder so the obligations are settled
before the dependency is added.

- Upstream: <https://github.com/basil00/WinDivert> (<https://reqrypt.org/windivert.html>)
- Copyright © Basil (basil00) and contributors
- License: **LGPLv3 / GPLv3 (dual)**. MasselGUARD intends the **LGPLv3** path:
  dynamic linking of an **unmodified** `WinDivert.dll`, preserving the user's
  ability to replace it (LGPL §4). When it is bundled, add here: WinDivert's own
  `LICENSE`/notice, plus copies of the **LGPLv3** and **GPLv3** license texts, and
  ship those files with the release.

---

## Fonts

Any bundled theme fonts (e.g. Orbitron, JetBrains Mono — SIL Open Font License)
live in the separate **MasselGUARD-themes** repository, not this one, and are
attributed there. If a font is ever shipped in this repository, add its OFL notice
here.
