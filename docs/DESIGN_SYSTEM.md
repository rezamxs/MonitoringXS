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

Application cards put identity and running state first, CPU and memory second, and supporting Process I/O, Physical disk, and Network data third. Metric labels remain quieter than their values. Long rate or unavailable text wraps instead of disappearing. Native list selection and focus visuals remain visible.

Running-application sorting stays inside the installed and portable sections. Measured values sort before unavailable states in both directions. Equal values use application name as a stable secondary key. Metric-driven reordering is limited to once every five seconds, while an explicit user change applies immediately. Reordering preserves the selected application, keyboard focus, and open tabs.

The app title bar uses the native WinUI `TitleBar` and system caption buttons. The app does not draw replacement minimize, maximize, or close buttons. The title bar remains the drag and double-click region so Windows keeps its normal window behavior.

Charts use crisp strokes, readable units, a bounded point count, and an explicit unavailable state. Destructive operations use `ContentDialog` with a direct data-loss warning.

## Motion and rendering

Motion is optional and generally 120-200 ms. Animate opacity and transforms only for small transitions. Never continuously animate the dashboard, large blur regions, chart backgrounds, or glow. Reduced-motion mode disables non-essential transitions.

## Accessibility

Use native controls, `AutomationProperties.Name`, visible focus visuals, logical tab order, scalable layout, and high-contrast resources. Test at 100-200% scaling and with keyboard-only navigation. Charts must have textual summaries.
