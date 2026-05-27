# MasselGUARD — Changes from v3.0.0 to v3.0.1

---

## Bug fixes

### Spurious error dialog on update

**Symptom:** After clicking the update button, two "Unhandled error" dialogs appeared showing `ResourceReferenceKeyNotFoundException: 'WindowBg' resource not found` and `ResourceReferenceKeyNotFoundException: 'TextMuted' resource not found`.

**Root cause:** When `RunUpdate()` calls `Application.Current.Shutdown()`, WPF's shutdown sequence triggers a final layout pass on still-open windows. During this pass, WPF tries to re-evaluate `{DynamicResource WindowBg}` and `{DynamicResource TextMuted}` bindings in the Settings window — but the theme `ResourceDictionary` is already being torn down. This raises `ResourceReferenceKeyNotFoundException`, which the global exception handler caught and displayed as an error.

**Fix:** `ResourceReferenceKeyNotFoundException` is silently suppressed in the `DispatcherUnhandledException` handler. It is always a harmless WPF shutdown artifact — genuine resource binding errors during normal operation manifest as blank controls, not unhandled exceptions.

---

### Theme preview revert

**Symptom:** Cancelling or timing out a theme preview did not correctly revert to the previously saved theme when Windows system colours (not a custom theme file) were active.

**Root cause:** `CancelThemePreview` and `CancelFontPreview` called `ThemeManager.Instance.Load(_originalTheme)` — but `_originalTheme` captured `ThemeManager.Instance.CurrentThemeName`, which may be a synthetic name (`__system__`) that does not map to a real theme file when the system palette is in use.

**Fix:** Both methods now call `_main.ApplyThemeFromConfig()`, which reads the committed `AppConfig` and correctly dispatches to either `LoadSystem(isDark)` or `Load(themeFile)` depending on `UseCustomTheme` and `SystemThemeMode`. The `_originalTheme` field is removed entirely.

---

## Appearance

### Settings → Appearance — System theme label

The card label was "Follow system theme" with a description referencing automatic switching. Renamed to **"System theme"** with a clearer description: *"Choose Light, Dark, or Auto to follow the Windows system preference."* Updated in all five languages.

### Preview buttons — fixed width

The three preview buttons (dark theme, light theme, font) now have a fixed `Width="76"` so that switching between `▶  Preview` and `↩  Xs` (countdown) text does not resize the button and cause adjacent sliders or selectors to shift.

### Activity log collapse button

The `»` collapse button in the activity log header is now `FontSize="22"` (was `11`) — twice as large for easier clicking.

---

## Startup — Shift emergency reset

Holding **Shift** at startup now resets **both** the font override and the custom theme:

- Font override: cleared (`FontOverrideEnabled = false`, family and size reset)
- Custom theme: reverted to Windows system colours (`UseCustomTheme = false`, `SystemThemeMode = "auto"`)

Each reset only fires if the respective setting was actually active. The confirmation dialog lists whichever resets were applied.
