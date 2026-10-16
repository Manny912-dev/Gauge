Gauge {VERSION} beta
================

A lightweight system monitor for Windows: CPU, GPU, memory, network, storage,
running processes, a full hardware spec sheet, and RGB lighting control. No account and no
sign-in. Gauge sends nothing about you or your PC; its only network use is checking
for and downloading its own updates, which you can switch off in settings.


Running it
----------
1. Unzip the whole folder somewhere (Desktop or Documents is fine).
2. Open the Gauge folder and double-click Gauge.exe.

Nothing needs installing. To remove Gauge, delete the folder.

Windows may show "Windows protected your PC" the first time, because this test
build is not code-signed yet. Click "More info", then "Run anyway".

Requires 64-bit Windows 10 or Windows 11.


Things to try
-------------
- The rail on the left switches pages: Monitor, Specs, Lighting, and Settings
  at the bottom. Ctrl+1 to Ctrl+4 do the same from the keyboard.
- Monitor: live load, temperature, clock and 90 seconds of history for the
  CPU and GPU. If you have more than one GPU, click the "1 / 2" chip to switch.
  Click a column heading in the process list to sort by it.
- "Restart as admin" (next to the process list) adds per-app network speeds
  (Down / Up columns).
- Specs: everything about the machine, detected automatically.
  "Copy specs" puts it on the clipboard as text.
- Lighting: your RGB devices are listed on the left; pick one to set its
  effect, colour, brightness and speed on the right. Click a zone in the
  preview to colour it on its own. Changes apply as you make them.
- Settings: accent colour, "Start with Windows" (opens Gauge in the tray by
  the clock when you sign in), "Keep running in the tray" (the close button
  hides Gauge instead of quitting) and updates. With "Apply when Gauge
  starts" on for a lighting device, its lighting is set at every sign-in.
  Click the tray icon to open Gauge; right-click it to quit.

Known limits in this build
--------------------------
- CPU temperature comes from the Windows thermal sensor. On some PCs that
  sensor is missing, and the Temperature tile will not appear.
- Fan speed only shows on hardware that exposes it. Most laptops do not.
- Started with Windows, Gauge runs without administrator rights, so the
  per-process Down / Up columns stay empty until you use "Restart as admin".
- Only the installed version (Gauge-Setup.exe) updates itself. A copy run from
  this zip stays on the version you downloaded. An installed copy waiting in
  the tray applies a downloaded update by itself and goes back to the tray.
- Lighting uses Gauge's own driver for Lenovo 4-zone laptop keyboards and a
  built-in OpenRGB engine for hundreds of other devices: keyboards, mice,
  headsets, coolers, fan and LED controllers. It finds them by itself a few
  seconds after Gauge starts; nothing extra needs installing. Hardware it
  cannot drive is listed under "No driver yet"; sending us the
  device report is how it gets added.
- Motherboard and RAM lighting is off until you choose "Turn on" under
  "Motherboard & RAM" on the Lighting page. That needs one administrator approval: it
  installs the PawnIO driver (downloaded from its official release) and runs the
  lighting engine with administrator rights from Program Files. "Turn off" in
  the same place removes it again. Uninstalling Gauge does not remove it, so
  turn it off first. PawnIO stays installed and can be removed from Windows'
  installed apps list. If a vendor lighting app (Vantage,
  iCUE and so on) is running, it may override what Gauge sets.
- Tested on one machine so far (AMD laptop with Nvidia graphics). Intel CPUs,
  AMD and Intel graphics, and desktop boards are exactly what we want to hear
  about.


Reporting a problem
-------------------
Please send back:
  1. What you did and what you expected.
  2. A screenshot if it is a visual problem.
  3. The specs text (Specs > Copy specs).
     If the problem is lighting, or your RGB hardware is not listed as
     supported, also send the device report (Lighting > No driver yet > Copy report).
  4. The log file. Press Win+R, paste this, and press Enter:
         %AppData%\Gauge
     and attach gauge.log from that folder.

Send these to whoever gave you this build.


Third-party software
--------------------
See THIRD-PARTY-NOTICES.txt.
