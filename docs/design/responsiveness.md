# Responsiveness - keep the UI thread free

AgentEyes must feel instant everywhere. The WPF UI thread paints the window and
handles input; anything slow that runs on it freezes the app. This is a standing rule,
reviewed on every change that touches the UI.

## The rule

1. **Never do slow work on the UI thread.** That includes file I/O, `ffmpeg`/`Process`,
   image decode, parsing many JSON files, network calls, and recursive directory deletes.
   Hand it to a worker thread and marshal the result back.

2. **Use the `Ui` helper** (`src/AgentEyes.App/Ui.cs`) so every call site reads the same:
   - `await Ui.Run(() => slowWork())` - run blocking work on the thread pool, await on the UI thread.
   - `Ui.Post(() => updateUi())` - marshal back to the UI thread from a worker.
   - `Ui.RunThenPost(work, onDone)` - fire-and-forget background work, then update the UI.
   This is the cc-director SynchronizationContext/Dispatcher pattern, wrapped.

3. **Give immediate feedback.** Any action that takes more than ~100ms shows a state
   change *the instant it is clicked* - a label, spinner, or status - never a silently
   disabled control. The recording HUD, for example, flips to "Finishing..." then
   "Transcribing..." rather than sitting greyed-out while the file muxes.

4. **Keep lists virtualized.** ItemsControls use a `VirtualizingStackPanel`. A **grouped**
   list (e.g. the Library's Today/Yesterday sections) must explicitly set
   `VirtualizingPanel.IsVirtualizingWhenGrouping="True"` and `VirtualizationMode="Recycling"`
   - grouping turns virtualization OFF by default, which silently realizes every row.

5. **Don't rebuild what you can toggle.** Swapping an `ItemsPanel`/`ItemTemplate` regenerates
   every container. Prefer toggling visibility or restyling over a full rebuild on a hot path.

## State (2026-06-08)

Applied:
- Recording **stop feedback**: the HUD shows "Finishing..." while the file finalizes (off the
  UI thread) and "Transcribing..." through Whisper, then closes - from the HUD, the main
  window, and any other stop surface (`HudWindow.SetStatus`, `MainWindow.StopAsync`).
- **Delete** recordings: rows drop instantly; the recursive folder delete runs on a worker.
- **Dictionary** load reads `dictionary.json` off the UI thread on the tab switch.
- **Library list** virtualizes even when grouped (the fix above).

Known follow-ups (not yet done):
- **Card (grid) view** uses a `WrapPanel`, which WPF cannot virtualize - fine for a small
  library, but a `VirtualizingWrapPanel` is needed before the library grows large.
- Search filter could debounce; dictionary **save** could move off the UI thread (both tiny today).

## Architecture note

The app is code-behind with manual `Ui` marshaling (not MVVM). That is deliberate for its
size. If a future view warrants MVVM, `CommunityToolkit.Mvvm` (`[ObservableProperty]` /
`[RelayCommand]`, as cc-director uses) is the path - it auto-marshals async command results
to the UI thread and would replace the manual `Ui.Post` calls in that view.
