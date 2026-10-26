# Testing EdgeSpan on a PC and a phone

You need a Windows 10 or 11 PC and an Android phone or tablet on the **same Wi-Fi**. Guest networks that hide devices from each other will not work. Put the phone next to the monitor, usually on the right.

These builds are for testing. The Android app is a release build signed with a test key, so Android may warn that it is not from the Play Store. The Windows program is a portable folder, not a setup wizard. Windows SmartScreen may say the publisher is unknown. Choose **More info**, then **Run anyway**.

## 1. Install the Windows app

The Windows build is split into pieces named `EdgeSpan-Windows-x64.7z.001`, `.002`, `.003`, and so on. Each piece is at most 20 MB. Windows File Explorer cannot open them. Use 7-Zip.

1. Install 7-Zip from https://www.7-zip.org/ if it is not already installed.
2. Put every `EdgeSpan-Windows-x64.7z.*` file in the same folder, for example `C:\EdgeSpan`. Do not rename them, and do not skip a part.
3. Right-click `EdgeSpan-Windows-x64.7z.001` and choose **7-Zip → Extract to "EdgeSpan-Windows-x64\\"**. 7-Zip reads the other parts by itself. You can also open a Command Prompt in that folder and run:

```bat
"C:\Program Files\7-Zip\7z.exe" x EdgeSpan-Windows-x64.7z.001 -oC:\EdgeSpan
```

4. Open the extracted folder and double-click `EdgeSpan.exe`. You do not need to install .NET.
5. A window opens. The icon also sits in the system tray. Closing the window hides it there on Windows. Use the tray menu to open it again.
6. The first launch starts a 14-day trial. You can keep testing without a license key.

## 2. Let EdgeSpan through the firewall

The first time EdgeSpan talks on the network, Windows may ask for permission. Allow it on **Private** networks.

If the phone never connects:

1. Open **Windows Security → Firewall & network protection → Allow an app through firewall**.
2. Allow `EdgeSpan.exe` for Private networks.
3. If there is still no connection, add an inbound rule that allows **UDP** for `EdgeSpan.exe` on ports **45454** (discovery) and **45455** (the session). If you change the session port under **Network**, allow that port instead. A port change applies the next time you open EdgeSpan.

Both devices must use the same Wi-Fi. A VPN on the PC can hide it from the phone.

## 3. Install the Android app

1. Copy `EdgeSpan-Android.apk` onto the phone (USB, Drive, or a mail attachment to yourself).
2. Open the file. If Android blocks it, allow installs from that app (Files, Chrome, or Drive) under **Install unknown apps**.
3. If Play Protect warns you, this is expected for a test build. Install anyway.
4. Open EdgeSpan. The first launch is a short walkthrough: accessibility, drawing over other apps, the EdgeSpan keyboard, and wireless debugging. Each page has a button that opens the matching Android setting. You can skip and come back later. Pick **English** or **فارسی** under Settings. Type a name for the phone if you want one.
5. If Android asks for notification permission, allow it. EdgeSpan stays running as a quiet notification while a session is active.

## 4. Turn on Accessibility mode

Do this before you press **Start**. This mode does not need a computer connection beyond Wi-Fi. The cursor you see is drawn by EdgeSpan. Clicks and drags are gestures.

1. On the phone, open **Settings → Accessibility**.
2. Under installed or downloaded services, open **EdgeSpan** and turn it on. Confirm the prompt. EdgeSpan does not read the screen.
3. Open **Settings → Apps → EdgeSpan → Display over other apps** (the wording varies) and allow it. The app also asks for this the first time you press **Start**.
4. Open **Settings → System → Languages & input → On-screen keyboard → Manage on-screen keyboards** and enable **EdgeSpan**.
5. When you want keys from the PC to type into a text field, switch the phone keyboard to **EdgeSpan**.

In the EdgeSpan app, leave the mode on **Accessibility**.

## 5. Pair with the PIN and QR code

1. On the PC, open **Pairing** and press **Start pairing**. A six-digit PIN and a QR code appear. They expire after a few minutes. Start pairing again if that happens.
2. On the phone, open the camera and scan the code on the PC. If Android offers to open EdgeSpan, accept. That fills in the pairing link.
3. On the phone, open **Pairing** and type the six-digit code. You can paste the full `edgestpan://pair` link instead. The phone says when it is looking, when it connects, or why it failed. A wrong code does not close the app.
4. On the same Wi-Fi the code is enough. Away from that network, turn on internet mode on the PC, set the relay address there, and enter the same relay address in the phone's Settings. Leave STUN blank unless you run your own STUN server. EdgeSpan does not use Google STUN.
5. Press **Pair**. The screen says it is looking, then that it connected, or it shows the reason it failed. A notification stays up while the session is connected.
6. On the PC, **Devices** should list the phone as paired. Overview should stop saying that no device is paired.

The PC and the phone both need to stay awake for the first try.

## 6. Set the edge

The first phone is attached to the **right** side of the PC. Coming back onto the PC is the **left** side of the phone screen. Change that if the phone sits somewhere else.

1. On the PC, open **Layout**. Drag the phone onto the edge or corner of the monitor where it physically sits. The glowing seam is the crossing. You can also select the phone and set the fields below.
2. **PC edge** is the side of the monitor the pointer leaves. **Return edge on device** is the side of the phone it crosses to come back. A phone on the right of the monitor uses PC edge **Right** and return edge **Left**.
3. Leave **Anchor** on **Edge**, **Offset** at 0, and **Span** at 0. Span 0 maps the PC edge one-to-one with the phone.
4. **Pointer speed** changes only while the cursor is on that phone. Start at 1.0.
5. Press **Apply**.

Move the mouse off the right edge of the monitor. The PC cursor parks, and a green cursor appears on the phone. Click, drag, and scroll there. Move the phone cursor off its left edge and the PC has the mouse again.

**Ctrl+Alt+S** switches screens. **Ctrl+Alt+L** locks the pointer on the current screen so it cannot cross. Press the lock shortcut again to unlock. You can change both shortcuts under **Network**.

## 7. Wireless debugging (native mouse and keyboard)

Use this when you want a real system cursor instead of the green overlay. It does not need root. Accessibility mode can stay installed. Switch the EdgeSpan app to **Wireless ADB** only after the server below is running.

1. On the phone, open **Settings → About phone** and tap **Build number** seven times. Enter your PIN if asked.
2. Open **Settings → System → Developer options**. Turn on **Wireless debugging**.
3. Tap **Pair device with pairing code**. Leave that dialog open. It shows an IP address, a port, and a six-digit code. That port is only for pairing.
4. On the PC, open PowerShell in the unzipped EdgeSpan folder and pair. Use the address and code from the dialog:

```powershell
.\platform-tools\adb.exe pair 192.168.1.20:37123 123456
```

5. Close the pairing dialog. On the main Wireless debugging screen, note the IP and port (often port 5555, and it is not the pairing port). Connect:

```powershell
.\platform-tools\adb.exe connect 192.168.1.20:5555
```

6. Start the injection server. The window must stay open. Closing it stops the virtual mouse and keyboard.

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-EdgeSpan-Server.ps1
```

You should see a line that the server is running. If `adb` was not already connected, you can pass the address instead:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-EdgeSpan-Server.ps1 -Address 192.168.1.20:5555
```

7. In the EdgeSpan app, press **Wireless ADB**, then **Start**. Pair again from the PC if the PIN expired.
8. Cross the edge as in section 6. The phone cursor is now the system pointer, and the keyboard is a virtual keyboard. You do not switch to the EdgeSpan keyboard for this mode.

If the server window reports that the device is unauthorized or offline, turn Wireless debugging off and on, pair again, and connect again. The pairing code changes every time you open that dialog.

## What to try

- Move off the configured edge and back. The PC should regain the mouse without a click.
- Click, drag, and scroll on the phone in both modes.
- Type into a text field. Accessibility mode needs the EdgeSpan keyboard selected. Wireless ADB mode does not.
- Copy a short piece of text on the PC and see whether it arrives on the phone. Clipboard sharing can be turned off under **Network**.
- Put the phone on the other side of the monitor, change the edges under **Layout**, press **Apply**, and cross again.
