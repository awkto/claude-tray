# claude-tray

A tiny, native Windows system-tray monitor for Claude usage limits.

Successor to / redesign of [Clawdmeter-Windows](https://github.com/weltern/Clawdmeter-Windows), rebuilt as a
proper desktop tray app instead of a ported dedicated-hardware dashboard.

## What it does

- **Tray icon** that changes color as you approach your limits (green → yellow → orange → red, badge at 100%).
- **Toast notifications** when any limit bucket crosses a configurable threshold (default **90%**).
- **Compact flyout** — click the tray icon and a small popup appears right next to the tray with
  **vertical bars**, one per limit bucket (5-hour session, 7-day all models, 7-day **Fable**, and any
  future scoped limits Anthropic adds), each with a percentage and reset countdown.
- **Streamlined auth** — sign in with your Claude account directly (OAuth), or auto-import existing
  Claude Code credentials, including from **WSL** (`\\wsl$\...\.claude\.credentials.json`). No manual
  JSON-file hunting.
- **Proper install** — signed installer, start-at-login option, auto-update check, winget (planned).

## How it gets data

Polls Anthropic's OAuth usage endpoint (`GET https://api.anthropic.com/api/oauth/usage`) — the same
endpoint Claude Code's `/usage` uses. The response contains a generic `limits[]` array:

```json
{
  "limits": [
    { "kind": "session",       "group": "session", "percent": 14, "severity": "normal", "resets_at": "..." },
    { "kind": "weekly_all",    "group": "weekly",  "percent": 11, "severity": "normal", "resets_at": "..." },
    { "kind": "weekly_scoped", "group": "weekly",  "percent": 16, "severity": "normal", "resets_at": "...",
      "scope": { "model": { "display_name": "Fable" } } }
  ]
}
```

The app renders **one bar per entry**, so new scoped limits (like the recently added Fable weekly limit)
show up automatically without an app update. No token-burning probe requests (unlike Clawdmeter's
1-token `/v1/messages` polls).

## Tech stack

- **.NET 8 / C# / WPF**, `H.NotifyIcon.Wpf` for the tray + flyout, Windows toast notifications.
- Published as a self-contained single exe; **Inno Setup** installer; **Authenticode code signing**
  (Azure Trusted Signing) in CI.
- **GitHub Actions**: build on push/PR; release on semver git tags (`v1.2.3`).

See [docs/DESIGN.md](docs/DESIGN.md) for the full design.

## Development status

Planning/scaffolding. Work is tracked as issues on a self-hosted GitLab:
`gitlab.dnsif.ca/github/claude-tray`.

## License

MIT
