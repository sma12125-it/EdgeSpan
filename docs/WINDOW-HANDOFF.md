# Window handoff

Each app keeps running on the device that owns it. Dragging its window across the shared edge shows that same window on the other device, as a normal movable window there. Closing it, minimizing it, or dragging it back returns it to the device it never left.

This is a view and an input path. It does not move the process, and it does not mirror the whole desktop.

Build 0.9.2 stays the baseline. Handoff is off until a phase below says it is ready. The USB mouse path from build 20 must keep working while this is built: video must not share the input socket.

## What “smooth” means

The number we will publish is the time from an input event on the viewing device to the frame that shows the result of that input.

| Link | Goal |
| --- | --- |
| USB | p50 under 50 ms on the test pattern |
| LAN, same switch | p50 under 50 ms on the test pattern |
| Internet | best effort. Direct punch can be close. A relayed path is not in the 50 ms budget. |

60 fps when the source window is changing and the encoder is hardware. An idle window sends nothing. Text is encoded at the window’s physical pixels, and the viewer shows those pixels 1:1 once the resize settles. DPI is sent with the window so a 150% Windows window and a DeX density do not get stretched by accident.

The pipe itself should be shorter than 50 ms. One source frame is about 17 ms at 60 Hz, and that time belongs to the app, not the codec. The budget for our pipe on USB:

| Stage | Target |
| --- | --- |
| Capture, GPU only | 2–8 ms |
| Hardware encode, no B-frames | 2–5 ms |
| USB or LAN | 1–5 ms |
| Hardware decode onto a surface | 2–6 ms |
| Present, waiting for the next scan | 0–17 ms |

A p50 near 30 ms of pipe plus one source frame is the shape of a passing USB run. p95 is recorded too. If a stage misses, the report names that stage instead of hiding it in one number.

## Two planes

Today’s session is UDP 45455, AES-GCM, and stop-and-wait reliable messages in 900-byte pieces, with a 48,000-byte ceiling. Mouse moves are unreliable and coalesced. That channel stays the control plane: offer, accept, geometry, input, close, minimize, return, and the latency stamps.

Video is a second plane.

| Link | Media |
| --- | --- |
| USB | `adb reverse` of TCP **27184**. Length-prefixed Annex-B. Its own socket, so a large frame cannot stall a click. |
| LAN | UDP, about 1200-byte packets, one-frame jitter. A gap asks for a new IDR and drops the broken frame. |
| Internet, direct | The same UDP, sent to the punched address. |
| Internet, relay | Not the datagram relay. A later raw TCP forward. Until that exists, handoff stays on USB or LAN and says so. |

Port 27183 remains the UHID server. Port 45455 remains the session. Port 8787/8788 remain signaling and the ciphertext relay.

Control messages are encrypted with the existing session keys. Media uses the same keys and a separate sequence space, so a video packet cannot replay as a mouse packet. A peer that does not advertise handoff never receives these messages, and a drag across the edge stays the pointer handoff that 0.9.2 already does.

Capability bit `0x08` on Hello means this peer can hand off windows. New reliable types:

| Type | Name | Role |
| --- | --- | --- |
| `0x60` | HandoffOffer | id, direction, source window, title, DPI, physical size, logical size, codec |
| `0x61` | HandoffAccept | id, chosen codec, viewer size |
| `0x62` | HandoffGeometry | move or resize, in physical pixels and DIPs |
| `0x63` | HandoffInput | pointer, wheel, key, or text commit, aimed at one id |
| `0x64` | HandoffState | focused, minimized, restored, closed |
| `0x65` | HandoffReturn | put the window back on its home display |
| `0x66` | HandoffVideoConfig | bitrate, frame rate, “send an IDR” |
| `0x67` | HandoffClock | frame id and capture timestamp, for the latency report |

`HandoffInput` is not `MouseMove`. The existing mouse packets still move the shared pointer. Mixing them would drag the PC cursor while a tablet finger is using one window.

## Windows to Android

The app keeps running in its HWND. The tablet shows a live picture of that HWND and sends input back to it.

### Starting a handoff

The shared edge is the one EdgeSpan already uses for the pointer. A handoff starts only while the user is dragging that window (`EVENT_SYSTEM_MOVESIZESTART` or an active move/size loop) and either:

- the pointer itself has crossed onto the device, or
- the window has traveled at least 80 px past the edge.

A window slid along the monitor, with the pointer still on the PC, is left alone. The glow and EdgeSpan’s own windows are never captured.

### Capture and encode

`Windows.Graphics.Capture` from the HWND, into a Direct3D 11 texture. No CPU copy.

The encoder takes that texture. Order: NVENC, then Quick Sync, then AMF. The Media Foundation knobs that matter are low-latency mode, B-frame count 0, look-ahead 0, and a short IDR interval (about one second, plus an IDR whenever the viewer asks). SPS and PPS ride with the stream.

H.264 is the first codec. HEVC is used only when both the PC encoder and the Android decoder advertise it. Software encode is a last resort, labeled as such, and is not expected to hold 60 fps.

Bitrate follows the window. A 1080p window full of text starts near 10 Mbps and may fall when the picture is calm. The viewer reports loss and how deep its decode queue is; the sender steps the bitrate. An unchanged frame is not sent.

### The Android window

`MediaCodec` decodes onto a `SurfaceView`. On API 30 and newer the codec is created with low-latency mode. The surface’s buffer size is the video size. While the user is still dragging, a brief scale is acceptable. When the drag ends, the source HWND is resized to the tablet window’s aspect so the steady picture is 1:1, not a scaled-up encode.

On a phone or a tablet in normal mode the window is an application overlay: movable, resizable, and it receives touch only inside its own bounds. In DeX it is a freeform activity. EdgeSpan is already `resizeableActivity`, and the emulator held a freeform resize without killing the process. The handoff activity uses that same path (`WINDOWING_MODE_FREEFORM`) so DeX’s own window chrome can move it.

Several HWNDs can be handed off. Each has an id, a surface, and a z-order that follows the last touch.

### Input back to the HWND

Pointer, wheel, and keys are posted to that HWND (`PostMessage`), mapped from the viewer’s client area into the window’s client pixels. `SendInput` is not the default: it would move the real PC cursor and steal keys from whatever the PC user is doing. A setting can turn `SendInput` on for a window that ignores posted input. Games that read raw input only are out of scope.

Resize on the tablet becomes `SetWindowPos` on the HWND, then the capture size follows. Close posts `WM_CLOSE`. Minimize posts `SC_MINIMIZE` and hides the surface; the decoder stays warm for a few seconds so restore is immediate. Dragging the tablet window back across the edge that faces the PC sends `HandoffReturn`. Windows puts the HWND back at the monitor rectangle saved when the handoff began, and Android drops the surface.

## Android to Windows

This direction is the scrcpy shape, and it needs the shell server we already run for UHID. A normal app cannot create a virtual display that other apps can be moved onto.

It requires Android 10 (API 29). minSdk stays 24. On an older phone the settings row explains that this direction is unavailable. The Tab S6 is Android 12, so it qualifies. Shipping this direction waits on one check on that tablet: a shell `createVirtualDisplay` must accept a surface and `cmd activity` must be able to move a task onto it. If One UI blocks that, this direction stops until we have another supported path. The Windows-to-Android direction does not depend on it.

### Gesture

On DeX, a freeform window whose bounds cross the shared edge starts the handoff. Accessibility already sees window bounds change.

On a phone, third-party code cannot grab the system caption of another app. The gesture there is: the PC pointer is on the phone, the foreground app is not the launcher, and the user drives that pointer back across the edge. That app is the one that moves to the virtual display. An explicit “Open on PC” action does the same thing when the gesture is ambiguous.

### Virtual display and encode

The shell server creates a virtual display at the app’s current physical size and density, with a `MediaCodec` input surface as its content. The task is moved onto that display. The phone’s main screen is no longer showing that app. Encoding is H.264 from the surface, realtime priority, low-latency where the API allows, no extra buffering, IDR about once a second.

The Windows window is a native HWND. Decode is hardware, through Media Foundation into a D3D11 texture, presented without a bilinear enlarge when the client size matches the frame. The title and the icon are that Android app’s own: the title from the accessibility window, the icon from the package, cached by package name.

Mouse, wheel, and keys are injected into that display id, not the main touchscreen. Text composition is a committed string on the same `HandoffInput` path; a full IME is later. Resizing the Windows window resizes the virtual display and reconfigures the encoder, so the app reflows instead of being scaled.

Dragging the Windows window back to the shared edge, or pressing the return control, moves the task to the default display and releases the virtual display.

## Clipboard and audio

Clipboard already syncs in both directions while a device is connected. A handed-off window becoming focused makes that device the clipboard endpoint. There is no second clipboard protocol. The existing toggle still turns it off.

Audio is off by default.

- PC to tablet: per-app loopback where Windows provides it, otherwise silence rather than the whole PC mix. Opus or AAC in 20 ms frames.
- Tablet to PC: playback capture for the virtual display’s audio.
- If audio is late, it is dropped. Video is not delayed to match it.

## Settings

A Handoff section on both apps:

- Off, PC windows on the tablet, tablet apps on the PC, or both. Default off.
- Quality: Sharp (native pixels), Smooth (hold 60 fps, may cut pixels), Saver (30 fps, lower bitrate).
- Codec: Auto, H.264, HEVC.
- Audio forwarding.
- The last measurement: link, p50, p95, and the stage that cost the most.

The readout looks like `usb p50 32 ms · p95 47 ms · pipe 18 ms · encode 4 ms`.

## How the number is measured

Two clocks, so a slow app is not blamed on the encoder.

1. **Pipe.** The capture clock is written into `HandoffClock` with the frame id. The viewer subtracts it at present time. This is capture to photons, without the app’s own think time.
2. **Input to the updated frame.** A test window flips a known pixel on mouse-down. The viewer records the input send time and the first decoded frame that contains the new pixel. That is the number compared with 50 ms.

Both run on USB and on LAN, for a quiet switch. Internet is measured the same way and reported without the 50 ms claim. The test pattern is part of the phase exit, not a lab-only script we forget.

## Phases

Each phase ends before the next one starts. A phase that misses its number is fixed or cut back. It is not papered over with a lower-resolution default.

### 1. One PC window, USB

Capture, hardware H.264, TCP 27184, one Android overlay, posted input, drag out and drag back. Exit: the test pattern’s p50 is under 50 ms on USB, and the text is the window’s own pixels.

### 2. LAN, several windows, DeX

UDP media, adaptive bitrate, close and minimize, more than one window, and a DeX freeform activity. Exit: the same 50 ms p50 on a quiet LAN, two windows at once, and a freeform resize that leaves the EdgeSpan process running.

### 3. One Android app on the PC, USB

The virtual-display spike on the Tab S6 comes first. Then MediaCodec, a D3D11 window, the app’s title and icon, input into that display, and drag back to the main display. Exit: p50 under 50 ms on USB, and the app is on the phone again after return.

### 4. Resize, DPI, keyboard, clipboard focus

Steady state is a resized source, not a scaled video. Keyboard and a text commit work in both directions. Clipboard follows the focused handoff window.

### 5. Audio and the internet path

Audio stays optional. Internet media uses the punched UDP path. A relayed TCP forward is specified only after USB and LAN are inside the budget. HEVC joins Auto when both sides have a hardware codec.

### 6. Polish

Idle frames are skipped. The cursor inside the foreign window matches the source. A failed encoder, a black protected window, or a refused virtual display is a sentence in the UI, not a frozen rectangle.

## Out of scope

- Mirroring the whole desktop or the whole phone.
- Moving an app’s process to the other device.
- A 50 ms promise on a relayed internet path.
- Exclusive fullscreen games and raw-input-only games.
- Software encode as the default.

## Risks

- Some windows capture black (DRM, secure desktop). The UI says so and leaves the window on the PC.
- A PC with no NVENC, Quick Sync, or AMF will not make the 60 fps bar. Phase 1’s exit criterion is a machine that has one of those.
- Samsung’s encoder and One UI’s virtual display are the unknowns. Phase 3 does not start on a guess.
- Posted input will not drive every toolkit. The `SendInput` option is the escape hatch, and it is off because it moves the real cursor.
- Putting video on the existing reliable session would regress USB clicks. That split is the constraint on every phase.
