# Windows installer

Monitoring XS ships as one x64, per-machine MSI built with WiX Toolset 7.0.0.

The beta MSI user interface remains English-only. WiX does not provide a
maintainable built-in Persian UI path for this package, so application
localization is kept independent from stable product identity, install paths,
service names, shortcuts, `ProductVersion`, and `UpgradeCode`.
The MSI contains self-contained .NET 10 and Windows App SDK payloads, so no
bootstrapper or runtime downloader is required. Windows Installer owns file,
shortcut, upgrade, rollback, repair, uninstall, and service lifecycle. This is
the smallest supported design that provides one UAC boundary and native
rollback; it deliberately has no updater, signing shim, or general-purpose
custom-action host.

## Build and versioning

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\installer\Build-Installer.ps1
```

The Release MSI is written below
`.artifacts\validation\installer-packaging\package`. Generated publish trees,
packages, logs, and validation evidence stay ignored.

`installer/InstallerVersion.props` is the single source for the three-part MSI
product version and ProductCode. For every release, increment the version and
replace ProductCode with a new GUID. Never change UpgradeCode for this product.
WiX SDK and extension versions are pinned exactly to `7.0.0`; automated major
upgrades to WiX 8 or later are forbidden. Upgrade WiX only after an explicit
license, compatibility, MSI-table, clean-install, upgrade, repair, and uninstall
review.

WiX Toolset 7 is used under the OSMF EULA v1.1. The repository accepts only the
WiX 7 EULA through `<AcceptEula>wix7</AcceptEula>` in the installer project; it
does not accept any other EULA. Compliance must re-review OSMF eligibility
before any commercial monetization and again when annual gross revenue reaches
USD 10,000.

## Installed behavior

The MSI installs `MonitoringXS.App` and `MonitoringXS.PrivilegedEtwBroker`
under `%ProgramFiles%\Monitoring XS`. It creates the mandatory shared Start
Menu shortcut and offers the shared Desktop shortcut as an opt-in feature. The
app remains `asInvoker`; setup never launches it elevated.

The Broker is installed through MSI service tables as
`MonitoringXS.PrivilegedEtwBroker`, LocalSystem, automatic start, own-process,
and vital. Its executable path is MSI-owned, and its arguments contain only the
validated interactive user SID, session ID, and optional logon SID. A small,
fixed-purpose custom action applies and verifies the unrestricted service SID
before MSI starts the service. Failure aborts and rolls back setup. No shell,
arbitrary command, SeDebugPrivilege, permanent helper, driver, or protocol/pipe
change is introduced.

MajorUpgrade stops and removes the older Broker before replacing files, then
installs and starts the current definition. Repair restores installer-owned
files, shortcuts, and service registration. Uninstall stops and deletes the
service and removes installer-owned files, shortcuts, and registry rows.
Windows Installer handles cancellation and rollback; exit code 3010 must be
reported as reboot required, never silently treated as complete.

User state is intentionally outside MSI ownership and is never removed by
repair, upgrade, or uninstall:

- `%LOCALAPPDATA%\MonitoringXS\settings.json`
- `%LOCALAPPDATA%\MonitoringXS\history.db` plus SQLite sidecars
- `%LOCALAPPDATA%\MonitoringXS\attribution-overrides.json`

No automatic data-cleanup option is provided. A user who wants full removal
must close Monitoring XS and explicitly remove `%LOCALAPPDATA%\MonitoringXS`.
Do not add that deletion to MSI custom actions.

Signing and certificate acquisition remain a separate release task. Never
present an unsigned or self-signed package as production-signed.
