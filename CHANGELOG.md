# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-04-09

### Added

- Channel\<T\> pipeline architecture: Scanner -> Hashing -> Transfer -> Verify
- XXH128 file hashing via System.IO.Hashing with SIMD acceleration (AVX2/SSE2)
- Parallel SFTP transfers via SSH.NET managed connection pool (1-8 streams)
- Atomic file writes: upload to `.tmp.{guid}`, verify remote hash, then POSIX rename
- Resume support with 10 MB SQLite checkpoints via ProgressStream wrapper
- Interactive file selection with Spectre.Console MultiSelectionPrompt
- Live TUI with per-file progress bars, transfer speed, ETA, status icons
- Keyboard controls during sync: P (pause), R (resume), Q (quit)
- TOML configuration file (`xsync.toml`) with CLI argument overrides
- SQLite WAL metadata store for hash cache, sync state, and run history
- Polly resilience: retry 5x exponential with jitter + 30-minute timeout
- AES-256-GCM SSH cipher preference for AES-NI hardware acceleration
- Structured JSON Lines logging with per-run log files
- Non-interactive fallback for piped/redirected terminals
- Smart file name truncation preserving file extensions
- Cross-platform single-file binaries (Windows x64, Linux x64, macOS ARM64)
- Code quality: Meziantou, Roslynator, AsyncFixer, Semgrep, PVS-Studio — zero warnings

[0.1.0]: https://github.com/dantte-lp/xsync/releases/tag/v0.1.0
