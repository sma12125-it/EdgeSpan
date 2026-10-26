# EdgeSpan

EdgeSpan shares one mouse and keyboard between a Windows PC and Android phones or tablets. Move the pointer off a chosen edge or corner of the PC and it continues on the Android screen. Come back through the matching edge and the PC has it again.

The Windows app is a system-tray settings program. The Android app can inject input in two ways: a native virtual mouse and keyboard over Wireless ADB, or an accessibility service plus the EdgeSpan keyboard when debugging is turned off. On a LAN the peers find each other and talk over encrypted UDP. Away from the LAN they try a direct UDP hole punch and fall back to a relay that only sees ciphertext.

The interface is English and Persian. Persian uses Vazirmatn and right-to-left layout. Latin text uses Plus Jakarta Sans. Motion, spacing, and color live in `docs/DESIGN-SYSTEM.md`. Springs are interruptible, and Reduce motion snaps them.

## Architecture

```text
Windows host                         Android
EdgeSpan.Windows (Avalonia)          app (Compose)
        |                                  |
        +---- EdgeSpan.Core session -------+---- protocol / ClientEngine
        |         AES-GCM UDP               |
        |                                  UHID server (app_process)
        |                                  or Accessibility + IME
        |
        +---- relay (Go) when the direct path does not open
              WebSocket signaling :8787
              UDP ciphertext      :8788
```

`EdgeSpan.Core` owns the packet codec, pairing, edge math, reliable delivery, discovery, STUN parsing, and the host session. The Android `protocol` module follows the same bytes. `ClientNode` in the core library is the reference client used by tests and `EdgeSpan.Sim`.

The host decides when the pointer crosses. Android confirms a return when its cursor hits the configured edge, so a lost packet cannot leave the pointer stuck.

## Requirements

- .NET SDK 8 to build the host, the simulator, and the tests
- Go 1.22 to build the relay
- JDK 21 and the Gradle wrapper in `android/` for the protocol tests
- An Android SDK (`ANDROID_HOME`) only when you want an APK
- Windows 10 or newer to capture a real mouse and keyboard
- Android 8 or newer for the phone app

This repository can be built and tested on Linux. The low-level hooks and both Android injection backends still need a Windows PC and an Android device.

## Build and test

```bash
dotnet test src/EdgeSpan.Core.Tests/EdgeSpan.Core.Tests.csproj -c Release
(cd relay && go test ./...)
(cd android && ./gradlew :protocol:test :server-core:test)
dotnet build src/EdgeSpan.Windows/EdgeSpan.Windows.csproj -c Release
```

`scripts/test.sh` runs that sequence. `scripts/e2e.sh` also starts the relay and checks `GET /health`.

## Run the Windows host

```bash
dotnet run --project src/EdgeSpan.Windows -c Release
```

On Windows the window closes to the tray. On Linux, closing the window exits. The settings file is `%AppData%/EdgeSpan/settings.json` (or the platform equivalent of the application-data folder).

1. Open **Pairing** and start a PIN. The QR contains the PIN because you are scanning your own screen.
2. On the phone, paste that link or type the PIN. Devices on the same LAN also appear under **Devices**.
3. Open **Layout**. Attach the phone to an edge or a corner, set the offset and span, and set the pointer speed for that device.
4. Leave the pointer through that edge. **Simulate leaving the PC** runs the same transition without a physical mouse, which is how the layout can be checked on a machine without the Windows hooks.

Ctrl+Alt+Space switches mouse and keyboard control between the PC and the phone without dragging to the edge. With more than one device it cycles them in order. Ctrl+Alt+1 through 9 jump to a device. Ctrl+Alt+L still locks the pointer on the current screen. Both shortcuts can be changed from Settings, including a record-shortcut control. The switch shortcut is also shown on Overview and in the tray menu. Saved settings that still say the old default Ctrl+Alt+S are read as Ctrl+Alt+Space.

While the phone has the pointer, a green glow sits on the PC edge or corner the cursor left. Color, intensity, and thickness are in Settings. Clipboard text sharing is on by default. Changing the session port applies the next time EdgeSpan starts.

Drag a window into the shared edge and keep pushing. Windows holds the cursor on the last pixel during a drag, so EdgeSpan starts the handoff from that pixel plus the outward raw movement. It cancels the native drag, parks the window off that edge, and continues the drag on the tablet over USB. If capture or the offer fails, the window is put back at its original position immediately. A second drag is ignored until that attempt finishes. A failure shows a toast with the reason on both sides (`handoffNeedTablet`, `handoffPcOff`, `handoffNoLink`, `handoffNoHwnd`, `handoffNoEncoder`, or `handoffBlack`).

If it still fails, export the diagnostics log. On Windows open About and choose **Export diagnostics log**. The same button is on the Network page. The file is `%AppData%\EdgeSpan\edgestpan.log`. On the tablet open Settings, About (درباره), and **Export diagnostics log** (خروجی گزارش عیب‌یابی), then share or paste the text. The tablet file is the app's private `edgestpan.log`.

A Windows build that uses the .NET 8 runtime already installed on the PC:

```bash
scripts/publish-windows.sh
```

`scripts/publish-windows.sh` writes `publish/win-x64`, including `adb.exe` and `edgestpan-server.dex`, then NSIS builds `publish/EdgeSpan-Setup-<version>.exe` (version from `Directory.Build.props`) with LZMA when `makensis` is installed. Inno Setup 6 can compile `installer/edgestpan.iss` on Windows instead (`iscc /DMyAppVersion=<version>`). The PC needs the .NET 8 runtime.

## Run the relay

```bash
cd relay && go run ./cmd/relay -http 0.0.0.0:8787 -udp 0.0.0.0:8788
```

The status page is `http://127.0.0.1:8787/` and `GET /health` returns JSON. Docker:

```bash
docker compose -f relay/docker-compose.yml up --build
```

In the host, open **Network**, enable internet mode, and set the relay URL to a server you run (`http://your-host:8787`). The phone needs the same address under Settings. A six-digit code is enough: on one Wi-Fi the phone finds the computer by discovery, and off the LAN both sides meet on that relay. The QR still carries the computer address when you can scan it.

STUN is optional and blank by default. A blank value sends binding requests to the relay itself, which answers them, so you do not need `stun.l.google.com` or any other Google service. To use a different server, type its host name. The relay must be reachable at a public address when the two devices are not on the same network.

The relay is intentionally not a WebRTC or QUIC peer. Mouse moves need an unreliable datagram. A reliable stream would add head-of-line delay. The relay never receives the session keys.

## Android

Protocol and server tests do not need an SDK:

```bash
cd android && ./gradlew :protocol:test :server-core:test
```

With `ANDROID_HOME` set, `scripts/build-android.sh` builds the UHID server dex and a release APK (`:app:assembleRelease`) signed with the development keystore (v1, v2, and v3). The script creates that keystore if it is missing:

```bash
mkdir -p android/keystore
keytool -genkeypair -keystore android/keystore/edgestpan-dev.keystore \
  -storepass edgestpan-dev -keypass edgestpan-dev -alias edgestpan \
  -keyalg RSA -keysize 2048 -validity 3650 -dname "CN=EdgeSpan Dev"
```

The store password is a development password. Do not ship it.

### Accessibility mode

1. Install the app and set the language.
2. Choose **Accessibility**.
3. Turn on the EdgeSpan accessibility service, allow drawing over other apps, and enable the EdgeSpan keyboard.
4. Paste the pairing link from the PC and press **Start**.

Clicks and drags become accessibility gestures. The cursor you see is drawn by EdgeSpan. Typed characters go through the input method. This mode does not use ADB.

### Wireless ADB mode

1. On the phone, turn on wireless debugging and pair `adb` from the PC.
2. `scripts/build-android.sh` writes `publish/android-server/edgestpan-server.dex` and copies the same bytes into the APK as `assets/edgestpan-server.bin`. The asset is not named `.dex`, because a packaged `.dex` entry makes some Android installers hang.
3. Push it and start it on the device. Leave that window open:

```bash
adb push publish/android-server/edgestpan-server.dex /data/local/tmp/edgestpan-server.dex
adb shell CLASSPATH=/data/local/tmp/edgestpan-server.dex app_process / com.edgestpan.server.ServerMain
```

On Windows, `scripts/Start-EdgeSpan-Server.ps1` does the push and start with the `adb.exe` shipped beside it. Step-by-step hardware instructions are in [docs/TESTING.md](docs/TESTING.md).

4. In the app, choose **Wireless ADB** and start the session.

The app sends decrypted events to `127.0.0.1:27183`. The server creates two virtual HID devices on `/dev/uhid` (an absolute mouse and a boot keyboard) with `BUS_VIRTUAL`. That path does not need root. The writes are the short 4376-byte CREATE2 events described in [docs/protocol.md](docs/protocol.md).

## Simulator

`EdgeSpan.Sim` is the reference Android client from the test suite, speaking the real UDP protocol:

```bash
dotnet run --project src/EdgeSpan.Sim -c Release -- --host 127.0.0.1 --port 45455 --pin 123456 --name Tablet
```

Start pairing on the host first so the PIN matches.

## License hook

This build checks licenses locally so a sales service can replace one class later. No key starts a 14-day trial on that PC. Deleting the settings file starts it over. A wrong key stays invalid. Development keys use the secret `edgestpan-dev-license-v1`, which must be replaced before anyone pays for a seat.

Sample development key: `ES-PR-K7M2Q9XW-B30C`

## What was verified here

Verified on Linux, without a Windows desktop session or an Android device:

- Packet codec, AES-GCM tamper and replay checks, reliable retry, fragment reassembly, pairing proofs, the RFC 5869 HKDF vector, discovery, STUN XOR-MAPPED parsing, relay frame layout, edge mapping, hysteresis, pointer speed, corners, lock, and clipboard
- An in-memory host-to-client session and a real UDP loopback of pairing plus an edge cross
- The Go relay, including a .NET client exchanging a signaling message and a UDP ciphertext payload
- Kotlin tests for the shared mouse-move vector, HKDF, an ECDH key agreement, the return-edge handoff, and the 4376-byte UHID CREATE2 layout
- The Windows Avalonia project and the simulator compile

Not verified here, because this environment cannot attach a Windows mouse hook or an Android device:

- `WH_MOUSE_LL`, `WH_KEYBOARD_LL`, raw input, cursor parking, and the tray on a real Windows desktop
- Accessibility gestures, the input method, the overlay cursor, and `/dev/uhid` on a device
- A phone and a PC on different NATs, including a live STUN server
- Installing the APK or the Windows package on physical hardware
- An Inno Setup installer (the portable Windows zip is the package to test)

## Next steps

1. Run the host on Windows and confirm raw input still moves onto a device when the cursor is clamped to the screen edge.
2. Install the Android package on a tablet and try accessibility mode, then the UHID server over wireless debugging.
3. Put the relay on a small VM, set its URL on both sides, and compare direct UDP with relay fallback from two different networks.
4. Replace `DevLicenseValidator` with the real licensing service before selling seats.
5. Add a sequence-number epoch before a session can run long enough to wrap a 32-bit counter.
