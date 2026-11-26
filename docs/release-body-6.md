# MasselGUARD 6 - Mending Meerkat

Paste the text below the line as the description of the GitHub release. Title: **MasselGUARD 6 - Mending Meerkat**, tag: **6.0** (no `v`; keep the dot, or installed 4.x copies cannot read the tag), created from `main`.

Assets to upload (all four, from one plain `BUILD.bat`): `MasselGUARD-x64.zip`, `MasselGUARD-x64.zip.sha256`, `MasselGUARD-arm64.zip`, `MasselGUARD-arm64.zip.sha256`.

---

A small release, mainly for installing the background service.

## Fixes
- **Tunnels with spaces in their name connect again** (they failed with "Cannot start service"). Behind the scenes the spaces become `_`.
- **Tunnel names are checked**: use A-Z, 0-9, spaces and `_ = + . -`, up to 32 characters. The app proposes a valid name.
- **Renaming a tunnel** now keeps your default tunnel, startup choice and automation rules pointing at it. History and data-usage totals stay under the old name.
- **Clearer message when the service cannot be installed**: when MasselGUARD refuses the install folder, the message now names the owner, or the account or group that has write access, and shows how to fix it.
- **Moved program files are repaired**: a file that was moved into the install folder keeps its old owner and permissions. When you install as administrator, and your own account is the only untrusted one on such a program file, MasselGUARD resets it to the folder's permissions and checks it again. Any other account or group is still refused.

## Other
- The third-party notices now list the licenses of the bundled WireGuard files and libraries, and the Apache-2.0 license text is included in the download. The built-in installer now copies these license files into the install folder.

## Upgrading
- Download the zip for your PC (`x64` or `arm64`), or update from inside the app.

Full details: [WHATSNEW](docs/WHATSNEW.md) and the [manual](docs/MANUAL.md).
