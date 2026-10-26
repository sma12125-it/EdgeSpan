# EdgeSpan protocol 1

EdgeSpan shares one mouse and keyboard between a Windows host and Android clients. This document matches the bytes implemented in `src/EdgeSpan.Core` and `android/protocol`.

Integers are little-endian. Strings are UTF-8 with a one-byte length, truncated on a code-point boundary. IPv4 is the tested path. IPv6 STUN XOR-MAPPED addresses are parsed, and the relay client uses IPv4.

## Datagram

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 1 | Magic `0xE5` |
| 1 | 1 | Version `1` |
| 2 | 1 | Type |
| 3 | 1 | Flags |
| 4 | 4 | Session id |
| 8 | 4 | Sequence |
| 12 | 4 | Ack |
| 16 | 8 | Timestamp, microseconds |
| 24 | 2 | Payload length |
| 26 | n | Payload |
| 26+n | 16 | AES-GCM tag, only when Encrypted is set |

The 26-byte header is the GCM additional data. A single payload is at most 48,000 bytes. Clipboard text is at most 256 KiB and is split into 900-byte chunks.

Flags: Encrypted `0x01`, Reliable `0x02`, Fragment `0x04`.

Types: Hello `0x01`, HelloAck `0x02`, PairInit `0x10`, PairChallenge `0x11`, PairConfirm `0x12`, PairAccept `0x13`, PairReject `0x14`, MouseMove `0x20`, MouseButton `0x21`, MouseScroll `0x22`, KeyEvent `0x23`, Clipboard `0x24`, EdgeEnter `0x30`, EdgeReturn `0x31`, Heartbeat `0x40`, Ping `0x41`, Pong `0x42`, Ack `0x43`, Goodbye `0x44`, Lock `0x50`, Unlock `0x51`.

A clear mouse-move packet with session 0, sequence 1, coordinates (10, -3) is:

```text
E5012000000000000100000000000000000000000000000008000A000000FDFFFFFF
```

## Payloads

Hello and HelloAck: `u8 role`, `u8 caps`, `u16 width`, `u16 height`, `u16 port`, `u8 idLen`, id, `u8 nameLen`, name. Role 1 is the Windows host. Role 2 is Android. Capability bits are advisory: `0x1` native UHID, `0x2` accessibility, `0x4` clipboard.

PairInit: 65-byte uncompressed P-256 public key (`0x04 || X || Y`), then id and name as above.

PairChallenge: 65-byte host public key and 16-byte nonce.

PairConfirm: 65-byte client public key and 32-byte client proof.

PairAccept: `u32 sessionId` (never zero), 16-byte salt, 32-byte host proof.

PairReject: one reason byte.

MouseMove: `i32 x`, `i32 y`.

MouseButton: `u8 button` (0 left, 1 right, 2 middle), `u8 down`, `i32 x`, `i32 y`.

MouseScroll: `i16 dx`, `i16 dy` in wheel ticks, `i32 x`, `i32 y`.

KeyEvent: `u16 virtualKey`, `u16 hidUsage`, `u8 modifiers`, `u8 down`, `u32 codepoint`. Modifier bits are Ctrl, Shift, Alt, Win from bit 0. HID usage 0 means the key has no HID mapping. The codepoint is the shifted character when EdgeSpan knows one.

Clipboard: `u32 id`, `u32 byteLength`, UTF-8 bytes.

EdgeEnter: `u8 returnEdge`, `i32 x`, `i32 y`, `u16 width`, `u16 height`. Edges are Left 0, Right 1, Top 2, Bottom 3.

EdgeReturn: `i32 hostX`, `i32 hostY`, `i32 deviceX`, `i32 deviceY`. A device that does not know the host pixel sends `int.MinValue` for the host coordinates. The host remains authoritative for the warp.

Ping and Pong: `u64` token.

Fragment wrapper, used when the Fragment flag is set: `u32 msgId`, `u16 index`, `u16 count`, chunk. Count is 1..64 and each chunk is at most 4,000 bytes. The first copy of a chunk wins. An incomplete message expires after 5 seconds.

## Pairing and encryption

The PIN never travels on the wire. Each side generates an ephemeral P-256 key. The shared secret is the raw X coordinate, left-padded to 32 bytes.

HKDF-SHA256 (salt is the UTF-8 PIN for the MAC key, and the 16-byte accept salt afterwards):

| Info | Output |
| --- | --- |
| `edgestpan-pair-v1` | 32-byte MAC key |
| `edgestpan-h2c-v1` | host-to-client traffic key |
| `edgestpan-c2h-v1` | client-to-host traffic key |
| `edgestpan-nonce-v1` | 4-byte nonce prefix |

Client proof is HMAC-SHA256 over `0x02 || nonce || clientPub || hostPub`. Host proof is HMAC-SHA256 over `0x01 || nonce || salt || hostPub || clientPub`.

The 12-byte GCM nonce is `prefix || direction || sequence u32 || 3 zero bytes`. Direction is 1 for host-to-client and 2 for client-to-host. Sequence numbers start at 1. Replay is a 64-packet window. Duplicate reliable packets are acknowledged again and not dispatched. A mouse packet older than the newest mouse sequence is dropped.

Reliable messages use stop-and-wait. The sender retries every 40 ms, up to 30 attempts, and sends the next reliable message only after the ack matches the in-flight sequence. Mouse moves are unreliable, unordered, and coalesced (default 2 ms). Keys, buttons, scroll, clipboard, and edge messages are reliable.

A pairing window lasts 5 minutes and allows 5 failures. The QR or pasted URI is:

```text
edgestpan://pair?v=1&h=HOST&p=PORT&pin=PIN&n=NAME&r=RELAYPEER&u=RELAYURL
```

`r` and `u` are present only when internet mode is on. The PIN is inside the QR because the person scanning it is looking at the host screen.

## LAN

Discovery is UDP port 45454, magic `E5 44`, version, role, caps, `u16 port`, `u16 width`, `u16 height`, id, name. Hosts also announce mDNS service `_edgestpan._udp.local`. The session default is UDP 45455.

## Edges

Each device is anchored to a PC edge or a corner. Offset is pixels from the start of that PC edge. Span 0 maps one-to-one with the device length along the return edge. Corner size defaults to 120 px. Entering through a corner places the pointer at the device center. Coming back from a corner lands on that PC corner pixel. An edge entry is inset 2 px. The return edge is not armed until the pointer is at least 8 px inside it, so the entry sample does not immediately bounce. Pointer speed is 0.1 to 8 and keeps a fractional accumulator.

The host decides the transition and also sends the unclamped coordinates, so the Android edge guard (`x < 0`, `x >= width`, `y < 0`, `y >= height`) confirms the return. Lock blocks transitions. Default hotkeys are Ctrl+Alt+S to cycle and Ctrl+Alt+L to lock.

## Internet

Mouse traffic stays on unreliable UDP because that is lower latency than a reliable SCTP or QUIC stream. The relay only forwards ciphertext.

Signaling is a WebSocket at `/v1/signal`. Client frames are masked. Messages are JSON:

| op | Meaning |
| --- | --- |
| hello | `{name, role}` from the peer |
| welcome | `{peerId, token, udpPort}` from the relay. Token is standard base64, 16 bytes |
| signal | `{to, data}` and the relay delivers `{from, data}` |
| list / peers | connected peers except the caller |
| error | `{message}` |

After both peers know each other's relay id (the Android app reads `r` from the pairing URI), they exchange:

```json
{"t":"announce","id":"...","lan":"192.168.1.5:45455","mapped":"203.0.113.8:45455"}
```

STUN binding requests are sent from the session socket. XOR-MAPPED-ADDRESS `0x0020` is preferred. Each side probes the other's LAN and mapped UDP addresses with a 4-byte `ESPN` datagram. A private LAN hit selects LAN. Any other hit selects direct. After 1.2 seconds with no hit, the path is the relay when internet mode is enabled.

The UDP relay frame is magic `E5 52`, version 1, op, 16-byte token, `u8 idLen`, id, payload. Op 1 binds a token to a peer id and the source address. Op 2 carries ciphertext to the destination peer id. Op 3 delivers it with the source peer id. A bind of token `11` repeated and id `ab` is hex `E5520101` + 32 ones + `026162`.

## Android injection

The accessibility backend draws an overlay cursor, dispatches gestures for click, drag, and scroll, and commits text through the EdgeSpan input method. No ADB pairing is required.

The UHID backend is a localhost server on port 27183 started with `app_process`. The app forwards decrypted events to it. The server writes short `uhid` events, not the full 4380-byte Linux struct, because a longer write returns `EINVAL` on 32-bit Android:

| Event | Bytes | Type |
| --- | --- | --- |
| CREATE2 | 4376 | `u32` 11, then the 4372-byte create2 body |
| INPUT2 | 6 + report | `u32` 12, `u16` size, report |
| DESTROY | 4 | `u32` 1 |

Inside create2, the name is at 0, `rd_size` at 256, bus at 258 (`BUS_VIRTUAL` = 6), vendor at 260, product at 264, and the descriptor at 276. The absolute mouse report is 6 bytes: 5 buttons, X and Y as `u16` in `0..32767`, and a signed wheel byte. The keyboard report is the 8-byte boot report.

The localhost frame is `u16 length`, then `u8 type` and payload. Type 1 is hello (16-byte token, `u16` width, `u16` height, `u8` mode). Types 3 to 6 are mouse, button, scroll, and key, using the same layouts as the encrypted payloads.

## License stub

Empty key: 14 days from `FirstRunUtc`. Deleting the settings file starts the trial again. A present but invalid key is invalid and does not fall through to the trial. Development keys look like `ES-PR-XXXXXXXX-YYYY` or `ES-FM-` for a family seat. `YYYY` is the first four hex characters of HMAC-SHA256 over `ES-PR-BODY` with the development secret `edgestpan-dev-license-v1`. Replace `DevLicenseValidator` before selling the product. The sample key `ES-PR-K7M2Q9XW-B30C` is for development only.

Sequence numbers are not wrapped in this version. Clipboard echoes are suppressed by remembering the last text applied locally.
