# Contributing to OpenBreak

Use [GitHub Issues](https://github.com/Colnnn/OpenBreak/issues) to report bugs or suggest an improvement. Search open issues first. For possible vulnerabilities, follow the [security policy](SECURITY.md).

## Working on a change

1. Follow the [build guide](docs/building.md) to clone the repository, install the pinned tools, and run the checks.
2. Keep each pull request focused on one change. For a larger change, open an issue first to discuss how it would fit the app.
3. Run the checks that cover the change. In the pull request, describe what changed, how you verified it, and any Windows behavior that still needs testing. Include screenshots with sample tasks for visible changes.

Use synthetic task titles and notes in examples, screenshots, and test data. Remove account names, local file paths, credentials, and task contents from any report you share.

## Source map

| Path | Contents |
| --- | --- |
| [`src/OpenBreak/`](src/OpenBreak/) | WinUI app. `Features/` holds Focus, Tasks, Capture, Breaks, and Appearance. `Controls/` holds shared UI. |
| [`src/OpenBreak/Scheduling/`](src/OpenBreak/Scheduling/) | Reminder and countdown behavior. |
| [`src/OpenBreak/Storage/`](src/OpenBreak/Storage/) | SQLite schema and persistence. |
| [`src/OpenBreak/Platform/`](src/OpenBreak/Platform/) | Windows tray, shortcuts, monitor placement, and launch integration. |
| [`tests/`](tests/) | Automated checks for app behavior, storage, and installer policy. |
| [`build/`](build/) and [`installer/`](installer/) | Build, packaging, and Windows installer inputs. |
