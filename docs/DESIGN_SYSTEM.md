# Precision Glass design system

Precision Glass is calm, technical, and application-centric. Depth supports grouping; it is not decoration.

## Tokens

- Base dark: graphite `#0B1017`; base light: cool neutral `#F3F6F8`.
- Elevated surfaces use opaque or lightly translucent neutral layers with solid fallbacks.
- Primary data accent: electric cyan `#28C7FA`.
- Selected/high-priority accent: restrained violet `#8B7CFF`, never a rainbow palette.
- Status colors are paired with text/icon/shape, never used alone.
- Radius scale: 4, 8, 12, 16 px. Spacing scale: 4, 8, 12, 16, 24, 32 px.
- Segoe UI Variable is preferred; metric numerals use tabular figures where available.

## Components

Application cards have one clear identity row, a compact metric grid, a textual status, and one primary selection action. Charts use crisp strokes, readable units, a bounded point count, and an explicit unavailable state. Destructive operations use `ContentDialog` with a direct data-loss warning.

## Motion and rendering

Motion is optional and generally 120-200 ms. Animate opacity and transforms only for small transitions. Never continuously animate the dashboard, large blur regions, chart backgrounds, or glow. Reduced-motion mode disables non-essential transitions.

## Accessibility

Use native controls, `AutomationProperties.Name`, visible focus visuals, logical tab order, scalable layout, and high-contrast resources. Test at 100-200% scaling and with keyboard-only navigation. Charts must have textual summaries.
