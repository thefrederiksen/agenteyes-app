# Issue #33 round 4 - the two platform facts the design rests on, MEASURED

The round-4 design records a size only when a person resizing the HUD is positively identified. That
rests on two claims about Windows and WPF that no unit test can prove, because they are facts about
the platform rather than about our code. Both were measured on this machine on 2026-08-28 with a
purpose-built probe (`uiaprobe-Program.cs.txt` beside this file: a WPF window in the HUD's own shape -
`WindowStyle.None`, `AllowsTransparency`, `ResizeMode.CanResize`, `MinWidth 260` - logging every
window message it receives and every automation-peer call it serves).

Machine: Windows 11 Enterprise 10.0.26200, .NET 8, x64.

---

## Fact 1: `WindowAutomationPeer` does NOT implement `ITransformProvider`, so UI Automation resizes a
## WPF window through the default HWND provider - with the same messages a layout pass produces

Run 1, with a peer that only logs `GetPattern`:

```
23:14:45.521 start; ITransformProvider on WindowAutomationPeer = False
23:14:45.846 SizeChanged 520x400
...
--- the client calls TransformPattern.Resize(1560, 400) ---
23:14:48.552 PEER GetPattern Transform -> null        <- WPF has no Transform pattern to offer
23:14:48.557 PEER GetPattern Transform -> null
23:14:48.614 MSG WM_WINDOWPOSCHANGED
23:14:48.615 MSG WM_SIZE
23:14:48.616 SizeChanged 1560x400
```

`typeof(ITransformProvider).IsAssignableFrom(typeof(WindowAutomationPeer))` is **False**. The resize
still happens - `rect after=100,100,1560,400` - but it reaches the app as `WM_WINDOWPOSCHANGED` +
`WM_SIZE` and nothing else. **That is byte-for-byte what a `Width =` assignment and a layout pass
produce.** This is the reason rounds 2 and 3 could not tell an accessibility tool's deliberate resize
from the window's own layout: at the point where the app was listening, the two are identical.

It is also why the round-3 test rig's `SetWindowPos` was an honest mistake. Its comment said "which
is exactly what UI Automation's TransformPattern.Resize does" - and it WAS, at the time.

## Fact 1b: a peer that DOES advertise the pattern takes the route back

Run 2, same probe, with `ProbePeer : WindowAutomationPeer, ITransformProvider` returning `this` for
`PatternInterface.Transform`:

```
23:16:03.664 PEER GetPattern Transform -> SELF
23:16:03.667 PEER GetPattern Transform -> SELF
23:16:03.694 PEER Resize 1560x400                     <- the intent arrives TYPED
23:16:03.695 MSG WM_WINDOWPOSCHANGED
23:16:03.696 MSG WM_SIZE
23:16:03.696 SizeChanged 1560x400
```

UI Automation prefers the WPF fragment provider's pattern over the HWND provider's. So the HUD serves
`TransformPattern` itself (`HudWindowAutomationPeer`), and an accessibility tool's resize command
arrives as a method call that no layout pass can make.

Confirmed end to end in the running app: `running-app-round4.txt` section (C), `CanResize=True`, the
resize lands, and the size is remembered.

---

## Fact 2: `WM_SIZING` is sent ONLY while a person drags a sizing edge

Run 3, same probe, driven into the keyboard resize-modal loop with `PostMessage(WM_SYSCOMMAND,
SC_SIZE)` + arrow keys + Enter (posted to the window's own queue; no global input synthesized):

```
23:17:04.627 MSG WM_WINDOWPOSCHANGED      <- the window being shown
23:17:04.628 MSG WM_SIZE
--- the modal loop ---
23:17:07.392 MSG WM_SYSCOMMAND wp=0xF000
23:17:07.393 MSG WM_ENTERSIZEMOVE
23:17:08.102 MSG WM_SIZING                <- x16, once per step of the drag
   ... 14 more ...
23:17:08.551 MSG WM_SIZING
23:17:09.028 MSG WM_WINDOWPOSCHANGED
23:17:09.028 MSG WM_SIZE
23:17:09.029 SizeChanged 597x400
23:17:09.032 MSG WM_EXITSIZEMOVE          <- the size is already final here
```

Three things this establishes, all load-bearing:

1. **`WM_SIZING` appears in the drag and nowhere else.** It is absent from the window being shown
   (`23:17:04`), from the peer-driven resize (`23:16:03`), and from every programmatic resize in
   every run. It is not a notification that the size changed - that is `WM_SIZE` - it is a
   notification that a human is dragging a sizing edge. A layout pass, a DPI change, a restore from
   minimise and a construction cannot produce it.
2. **`WM_SIZE` arrives BEFORE `WM_EXITSIZEMOVE`**, so the window's `ActualWidth`/`ActualHeight` at
   `WM_EXITSIZEMOVE` is the size the person let go of. That is why the size is read there.
3. **`WS_THICKFRAME` is set** (`style=0x160F0000`), so the chromeless HUD really is border-draggable
   and this path is reachable by a real person, not only by the grip.

A window MOVE runs the same modal loop and ends with the same `WM_EXITSIZEMOVE`, with no `WM_SIZING`
in between. That is why `HudUserResize` requires the `WM_SIZING`, and why
`HudUserResizeTests.AMoveOfTheWindow_RecordsNothing` exists. Confirmed in the running app:
`running-app-round4.txt` section (D) - two moves with the panel up leave `HudWidth`/`HudHeight` null.
