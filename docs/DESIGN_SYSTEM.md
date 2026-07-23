# Precision Glass design system

Precision Glass is calm, technical, and application-centric. Depth supports grouping; it is not decoration.

## Tokens

- Base dark: graphite `#0B1017`; base light: cool neutral `#F3F6F8`.
- Elevated surfaces use opaque or lightly translucent neutral layers with solid fallbacks.
- Primary data accent: electric cyan `#28C7FA` on dark surfaces and a darker cyan on light surfaces for readable text.
- Selected/high-priority accent: restrained violet `#8B7CFF` on dark surfaces, with a darker light-theme variant; never a rainbow palette.
- Status colors are paired with text/icon/shape, never used alone.
- Radius scale: 4, 8, 12, 16 px. Spacing scale: 4, 8, 12, 16, 24, 32 px.
- Segoe UI Variable is preferred; metric numerals use tabular figures where available.

## Components

Application cards use two tiers. The upper tier keeps application identity and running state dominant, with CPU and memory in compact primary panels. The lower tier keeps Process I/O, Physical disk, and Network visually secondary. Matching unavailable directions collapse to one honest state, while supporting text preserves `Access denied`, `Partial`, `Warming up`, `Unsupported`, or other availability detail. Partial values retain their measured lower bound. Native list selection and focus visuals remain visible.

Running Apps uses one compact toolbar for sorting and Advanced mode. The content column is fluid up to approximately 1120 px and must not introduce horizontal scrolling. The active navigation item combines the native selection marker, a background shape, and the standard keyboard focus indication instead of relying on color alone.

Running-application sorting stays inside the installed and portable sections. Application name starts at A to Z. A newly selected numeric metric starts at Highest to lowest, and the user can reverse either direction with a clearly labeled control. Measured values sort before unavailable states in both directions. If every visible value for a metric is unavailable, the toolbar reports `No comparable data` and uses application name as the deterministic order instead of treating missing values as zero. Equal measured values also use application name as a stable secondary key. Metric-driven reordering is limited to once every five seconds, while an explicit user change applies immediately. Reordering preserves the selected application, keyboard focus, and open tabs.

The app title bar uses the native WinUI `TitleBar` and system caption buttons. The app does not draw replacement minimize, maximize, or close buttons. The title bar remains the drag and double-click region so Windows keeps its normal window behavior. Caption-button foreground, inactive, hover, and pressed colors follow the app's effective Light or Dark theme. High Contrast returns those colors to Windows.

Charts use crisp strokes, readable units, a bounded point count, and an explicit unavailable state. CPU history retains UTC timestamps, orders samples, keeps the last duplicate timestamp, rejects invalid numeric values, and leaves unavailable intervals as visible gaps instead of connecting them or converting them to zero. Destructive operations use `ContentDialog` with a direct data-loss warning.

Appearance has exactly three modes: System, Light, and Dark. The UI describes System as `System — follows Windows` and reports the resolved state as `Currently Light` or `Currently Dark`; System is not a fourth palette. Light uses cool neutral surfaces rather than pure white throughout; Dark separates graphite window, navigation, toolbar, card, and chart surfaces. Electric Cyan remains the data accent, while Violet is limited to selection and priority. High Contrast resources map to the user's `SystemColor*` choices.

## Motion and rendering

Motion is optional and generally 120-200 ms. Animate opacity and transforms only for small transitions. Never continuously animate the dashboard, large blur regions, chart backgrounds, or glow. Reduced-motion mode disables non-essential transitions.

## Accessibility

Use native controls, `AutomationProperties.Name`, visible focus visuals, logical tab order, scalable layout, and high-contrast resources. Test at 100-200% scaling and with keyboard-only navigation. Charts must have textual summaries.
