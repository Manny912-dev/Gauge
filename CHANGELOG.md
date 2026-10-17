# Changelog

## 0.8.3

- **A stuck CPU temperature is no longer shown as live.** Without administrator rights, many desktops can only offer the motherboard's ACPI reading, which often never changes. When that reading stays exactly the same for 20 seconds, Gauge stops showing it and offers "Restart as admin" in its place, like the per-app network hint. If the PawnIO driver is not installed, where restarting would not help, it shows "–" instead. A reading that moves, as on most laptops, is shown as before.
- **Fixed:** opening Gauge again while a copy runs as administrator could end in an error instead of bringing that window forward.

## 0.8.2

- **CPU temperature tries every CPU sensor.** Gauge used to pick one temperature sensor at launch and, if that sensor gave no reading, fall back to the motherboard's thermal zone, which on many desktops reports a fixed number. It now takes the best sensor that is actually reporting each second (package, hottest core, core average, then each core), so the reading follows the CPU on more machines.
- **Clearer diagnostics.** The log notes which source the CPU temperature comes from whenever it changes, and `--sensor-probe` shows it on every line along with whether Gauge is running as administrator.

## 0.8.1

- **Fixed: CPU temperature and clock stopped updating** a few seconds after launch whenever Gauge ran without administrator rights (for example after an update restarted it). They now follow the CPU live. Running Gauge as administrator still gives the most accurate CPU readings.

## 0.8.0 — the Kraken's screen

- **NZXT Kraken Z3 screen.** Gauge can draw the 320 × 320 screen on Kraken Z53, Z63 and Z73 coolers without NZXT CAM: a gradient ring with a dot at the reading (or a full-screen gradient), the number, its unit and a label. It can show liquid, CPU or GPU temperature, load or clock, in five colour pairs or any two hues, at four brightness levels and four rotations. A live preview on the Lighting page shows exactly what the cooler will show, along with the liquid temperature and pump speed.
- Gauge leaves the screen to the Kraken until you choose Gauge. Only one program can hold the cooler, so Gauge waits while NZXT CAM is running and says so, and it hands the screen back to the Kraken when you switch back or quit.

## 0.7.2 — Keychron Q1 HE

- **Keychron Q1 HE lighting.** Gauge now drives the Q1 HE's RGB through VIA, the protocol in its stock QMK firmware: all 22 of its effects, colour, brightness and speed. Gauge reads what the keyboard is showing when it starts, and saves your change on the keyboard a moment after you stop adjusting. New and not yet tried on a real Q1 HE, so reports are welcome.
- **Better device reports.** For QMK keyboards and dongles Gauge cannot drive yet, the report now includes what their VIA interface answers, which is what adding them needs.

## 0.7.1 — a new mark

- **New logo.** A segmented dial: nine segments, six of them lit, like a meter two thirds of the way up. It is on the app icon, the taskbar and tray, the navigation rail and the tray menu.
- **A new installer panel.** The setup window now matches the app: the new mark and name, one line about Gauge, the version being installed, and a progress bar that fills a track built into the design.

## 0.7.0 — the redesign

A full redesign of the interface on the way to 1.0.

- **New layout.** A navigation rail on the left (Monitor, Specs, Lighting, and Settings at the bottom) and a page header with the page's own action. `Ctrl+1` to `Ctrl+4` switch pages.
- **Monitor.** Larger CPU and GPU readings with 90 seconds of history drawn edge to edge, details in a tidy grid, and temperatures that turn amber and red as they climb. Memory, network and storage share one panel. "Restart as admin" now sits by the process list, where per-app network speeds appear.
- **Lighting.** Rebuilt as a device list and an inspector. Every device shows a swatch of its current colours; the preview glows in the colours it is set to; effect, colour, brightness and speed are laid out as labelled rows. Motherboard and RAM setup and the "No driver yet" list live in the device column.
- **Specs.** Grouped, easier-to-scan sections and a single row of key figures.
- **Settings** is now a full page: accent colour, start-up and tray options, and update status.
- One consistent visual system throughout, and the Lighting, Specs and Settings pages scroll when the window is small.

## 0.6.1

- On Windows 11 the window has rounded corners and a matching outline, like the system's own windows.

## 0.6.0

- **Start with Windows.** Gauge can open in the notification area when you sign in, without its window, so saved lighting is applied at every sign-in.
- **Keep running in the tray.** The close button can hide Gauge instead of quitting it. Click the tray icon to open Gauge; right-click it to quit.
- Opening Gauge again brings back a hidden window, and a copy waiting in the tray installs downloaded updates by itself.
