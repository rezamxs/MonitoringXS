# Monitoring XS 0.9.0-beta.1 — Public Beta Release Notes

## What's new

- English and Persian runtime localization with automatic RTL layout
- Application search across installed and portable apps
- Sortable application list by name, CPU, memory, disk, network, and GPU
- Diagnostics page showing monitoring health at a glance
- Metric explanations with beginner-friendly descriptions
- Stable process selection with identity verification
- Process safety improvements with confirmed attribution before actions
- Real CPU monitoring with per-application usage
- Memory monitoring with working set tracking
- Physical disk read and write monitoring
- Network send and receive monitoring
- GPU monitoring where supported by Windows providers
- Local metric history with configurable retention
- Intentional chart gaps for missing samples — no fake data
- Windows installer support for easy setup
- System Overview data foundation for future system-wide insights

## Beta limitations

- Some metrics depend on Windows performance providers and may not be available on all systems
- Hardware and driver support can vary; GPU metrics require compatible drivers
- Advanced metrics may require elevated permissions or the privileged monitoring service
- Metrics may report Warming Up, Partial, Unsupported, Unavailable, or Error when data cannot be collected
- Missing samples are shown as intentional gaps in charts rather than interpolated values
- Unavailable data is never replaced with fake zero or fabricated values
- This is beta software. Some defects may remain.

## Platform

- Windows 10 (1809+) and Windows 11
- x64 and ARM64

## License

MIT