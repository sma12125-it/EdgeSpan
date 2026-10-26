# EdgeSpan motion and design

One system on Windows and Android. Motion is a spring, never a blocking timer. Input is applied the moment the edge logic says so. Paint catches up on its own thread.

## Springs

Mass is 1. Stiffness and damping come from a response time and a damping fraction, the way Apple describes a spring:

- stiffness `k = (2π / response)²`
- damping `c = 4π · dampingFraction / response`

Integrate with a semi-implicit Euler step so a new target keeps the current velocity. A gesture, a second tap, or a crossing in the other direction retargets. It does not restart from zero.

| Token | Response | Damping | Use |
| --- | --- | --- | --- |
| Press | 0.16 s | 0.92 | Button scale 0.97 and highlight |
| Standard | 0.28 s | 0.86 | Pages, toggles, checkmark, numbers |
| Breakthrough | 0.32 s | 0.72 | Edge membrane, shatter, window settle |
| Toast | 0.30 s | 0.84 | Glass toast slide and fade |

Durations that are not springs stay inside 150–350 ms. Press is the short end. Breakthrough is the long end, about 320 ms, and it never gates the pointer.

Easing, when a spring is the wrong tool (a one-shot opacity on a page that is leaving): `standard` is cubic-bezier(0.2, 0.8, 0.2, 1) and `exit` is cubic-bezier(0.4, 0, 1, 1).

Reduce motion is on when the system asks for it or the in-app switch is on. A reduced spring snaps to the target and clears velocity. Sound stays off unless its own switch is on.

## Space, radius, color, type

Spacing: 4, 8, 12, 16, 24, 32, 40. Snap layout to physical pixels.

Radius: 8 small, 12 controls, 16 cards and toasts, 22 large, pill 999.

Color, dark then light:

- Background `#202020` / `#F3F3F3`
- Card `#2C2C2C` / `#FFFFFF`
- Ink `#FFFFFF` / `#1A1A1A`
- Muted `#C8C8C8` / `#5D5D5D`
- Line `#3A3A3A` / `#E5E5E5`
- Accent `#60CDFF` / `#0067C0`
- Mint `#3DDC84` / `#0E7A3D`
- Gold `#F0B429` / `#A87408`

Type: Plus Jakarta Sans for Latin, Vazirmatn for Persian. Sizes 12, 14, 18, 30. Persian sets layout direction to right-to-left and mirrors the nav, cards, and edges. Icons are one 1.6 px stroke set, round caps, and they mirror with the layout.

Windows 11 uses Mica for the main window. Older Windows and Linux keep the solid background. Toasts are acrylic glass: 16 px radius, slide and fade on the Toast spring.

Buttons press to scale 0.97 with a soft highlight. Toggles, page changes, connect cards, and the pairing check share Press or Standard. The edge glow and the breakthrough share Breakthrough. None of these run on the input hook.
