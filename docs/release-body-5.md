# MasselGUARD 5 - Background Badger

Paste this as the description of the GitHub release. Title: **MasselGUARD 5 - Background Badger**, tag: **5.0** (no `v`, like the existing tags such as `4.6.0`, which is also what the Scoop `autoupdate` URL template expects; the tag needs the dot so that installed 4.x copies can see it; later releases can be `6`, `7`).

Tip: publish a test build as a **pre-release** to keep it hidden from the in-app updater (it follows GitHub's latest non-pre-release release).

Assets to upload (all four, from one plain `BUILD.bat`): `MasselGUARD-x64.zip`, `MasselGUARD-x64.zip.sha256`, `MasselGUARD-arm64.zip`, `MasselGUARD-arm64.zip.sha256`.

---

MasselGUARD can now do its privileged work in a **Windows service**, so the app starts without a UAC prompt and your network rules keep running when the window is closed. Without the service everything works exactly as before (portable copies, Scoop and no-service installs keep elevating the app themselves).

## Highlights

**Background service**
- **One-click install** in *Settings > Startup > Background service* (also offered by the installer and the setup wizard). Windows asks for administrator approval once; after that MasselGUARD starts without UAC.
- **Automation keeps running with the window closed** (optional, *Settings > Automation*): connect or disconnect a tunnel and switch DNS from your network rules, even before you sign in.
- **A timed DNS override survives closing the window**, and the **Explorer right-click DNS bypass works without the window and without a terminal**.
- **Start with Windows without administrator rights** (a per-user startup entry).
- **Tunnel configs stay in the service**, in a machine-encrypted store only SYSTEM and administrators can read.
- **Safer by design**: the service refuses to start from a folder that standard users can write to, creates its data folders with a fixed owner and permissions, and only touches network adapters and tunnel services it should. `MasselGUARDcli service allow|deny|users` manage who may use it.

**Browse DNS servers**
- A searchable picker of public DNS resolvers (replaces the fixed "Add presets"): add **and remove** servers with a checkbox, see what each one blocks and logs in your language, and add servers that need your own value (NextDNS) by filling in a field. About 40 servers from 17 providers to start with.
- **Test speed** shows how fast each server answers from your PC (optionally only the ticked ones) and sorts fastest first.
- The list lives on GitHub ([masselink/MasselGUARD-dnslist](https://github.com/masselink/MasselGUARD-dnslist), pull requests welcome) and updates without a new app version.

**Bypass shortcut**
- Configurable, new default **Ctrl+Alt+D**, optionally system-wide; pressing it again stops the bypass.
- A **default bypass length** setting, and a tray message when the bypass starts, stops or ends.

**Other**
- **Test rule** now opens a window with the verdict and a line-by-line explanation.
- **Add current network** (was "From network") on the Automation panel.
- **DNS leak protection** is clearer: a status (Protected / Not protected) and one button per Windows policy.
- The **release number** shows next to the name in the title bar, in Settings > About and in the setup wizard.
- **Verified updates**: every release ships a `.sha256` next to each zip, and the in-app updater checks it.
- Installing or updating while the app, the service or a tunnel is running no longer fails with "file in use".
- **Versioning**: releases are now a single number (5, 6, 7...).

## Upgrading
- Download the zip for your PC (`x64` or `arm64`), or update from inside the app.
- To use the service: *Settings > Startup > Background service > Install service*.
- Keep MasselGUARD in a normal folder such as `C:\Program Files\MasselGUARD`, not in OneDrive.

Full details: [WHATSNEW](docs/WHATSNEW.md) and the [manual](docs/MANUAL.md).
