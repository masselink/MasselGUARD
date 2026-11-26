# MasselGUARD 6 - Mending Meerkat

Paste the text below the line as the description of the GitHub release. Title: **MasselGUARD 6 - Mending Meerkat**, tag: **6.0** (no `v`; keep the dot, or installed 4.x copies cannot read the tag), created from `main`.

Tip: publish a test build as a **pre-release** to keep it hidden from the in-app updater.

Assets to upload (all four, from one plain `BUILD.bat`): `MasselGUARD-x64.zip`, `MasselGUARD-x64.zip.sha256`, `MasselGUARD-arm64.zip`, `MasselGUARD-arm64.zip.sha256`.

---

A maintenance release: tunnels with spaces in their name can connect, MasselGUARD takes more care about which tunnel service it stops, and installing the background service is easier to get right.

## Highlights

**Tunnel names**
- **Names with spaces work.** A tunnel such as `2 Block Full` failed with *Cannot start service*. MasselGUARD now uses `2_Block_Full` behind the scenes and keeps showing your name in the app.
- **Names are checked up front:** letters A-Z, digits, spaces and `_ = + . -`, up to 32 characters. The editor and import suggest or apply a valid name, and two names that would share one service (such as `a b` and `a_b`) cannot both be added.

**Safer connect and disconnect**
- MasselGUARD checks that a tunnel service **belongs to that tunnel** before stopping or replacing it. If a disconnect fails, the kill switch stays on and the log says why.
- A **connected tunnel can't be renamed**. Renaming a disconnected one now keeps your default tunnel, open-network tunnel, startup choice and automation rules pointing at it (history and data-usage totals stay under the old name).

**Background service**
- When the install folder is refused, the message now **says who has access and how to fix it**.
- A program file that was **moved** into the install folder (and kept its old permissions) is repaired during an administrator install, when your own account is the only untrusted one on it.

**Licenses**
- Updated third-party notices. The release now includes the Apache-2.0 license text, and the built-in installer copies the license files into the install folder.

## Upgrading
- Download the zip for your PC (`x64` or `arm64`), or update from inside the app.
- If a tunnel uses other characters than the ones above (for example `ü` or brackets) or is longer than 32 characters, WireGuard cannot use that name. The log suggests a new one; rename the tunnel in its editor.

Full details: [WHATSNEW](docs/WHATSNEW.md) and the [manual](docs/MANUAL.md).
