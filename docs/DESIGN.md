# claude-tray — Design

## Goals

1. Native, lightweight Windows tray app for Claude usage limits (Pro/Max subscription limits).
2. Alert the user *before* they hit a limit — configurable threshold, default 90%.
3. At-a-glance status from the tray icon alone (color states).
4. A small, tasteful flyout near the tray — not a full dashboard window.
5. Frictionless auth: OAuth sign-in or automatic credential discovery (incl. WSL).
6. Trustworthy distribution: signed binaries, real installer, semver releases via CI.

Non-goals (v1): mascot animations, per-session transcript analytics, cost/value stats,
macOS/Linux ports. Keep it small.

## Architecture

```
claude-tray.exe (.NET 8, WPF, single process)
├── TrayIconService      – renders dynamic icon, tooltip, context menu
├── FlyoutWindow         – borderless popup anchored to tray, vertical bars
├── UsagePoller          – timer → GET /api/oauth/usage (default every 60 s)
├── AuthService          – OAuth PKCE sign-in, token refresh, credential import
├── NotificationService  – Windows toasts, threshold crossing + re-arm logic
├── SettingsService      – %AppData%\claude-tray\settings.json
└── UpdateChecker        – GitHub Releases API, daily
```

### Data source

`GET https://api.anthropic.com/api/oauth/usage`
Headers: `Authorization: Bearer <access_token>`, `anthropic-beta: oauth-2025-04-20`.

Primary model: the `limits[]` array — each entry has `kind`, `group`, `percent`, `severity`,
`resets_at`, and optional `scope.model.display_name` (e.g. `"Fable"`, `"Opus"`). Render one
bar per entry; label from kind/scope:

| kind            | scope        | label      |
|-----------------|--------------|------------|
| `session`       | –            | Session    |
| `weekly_all`    | –            | Week · All |
| `weekly_scoped` | model=Fable  | Week · Fable |

Unknown kinds render generically from `group` + scope so future limits appear without app updates.
`extra_usage` / `spend` (overage credits) shown as a footer line when enabled.

### Auth

Three paths, in order of preference:

1. **Sign in with Claude** (primary): OAuth 2.0 Authorization Code + PKCE against the same public
   client Claude Code uses — authorize at `https://claude.ai/oauth/authorize`, token exchange at
   `https://console.anthropic.com/v1/oauth/token`, loopback redirect (`http://localhost:<random-port>/callback`),
   scopes `user:profile user:inference`. Opens the default browser; no file paths involved.
2. **Import Claude Code credentials**: auto-scan
   - Windows: `%USERPROFILE%\.claude\.credentials.json`
   - WSL: enumerate distros via `wsl.exe -l -q`, probe `\\wsl$\<distro>\home\<user>\.claude\.credentials.json`
3. **Manual paste** of an access/refresh token (fallback).

Tokens are stored in **Windows Credential Manager** (DPAPI), never in plaintext config.
Access tokens auto-refresh via the refresh token (~8 h expiry). On refresh failure → tray icon
gray + "sign in again" toast (once).

### Tray icon states

Icon is rendered at runtime (GDI+ → ICO handle): a vertical bar/gauge glyph filled to the **worst
bucket's** percentage, colored by state:

| state    | condition (worst bucket)     | color                    |
|----------|------------------------------|--------------------------|
| ok       | < warn threshold             | green `#22C55E`          |
| warn     | ≥ 60% (configurable)         | yellow `#EAB308`         |
| high     | ≥ 80% (configurable)         | orange `#F97316`         |
| critical | ≥ 90% (configurable)         | red `#EF4444`            |
| exceeded | ≥ 100%                       | red + "!" badge          |
| stale    | poll failing / signed out    | gray                     |

Tooltip: one line per bucket, e.g. `Session 14% · Week 11% · Fable 16%`.
Context menu (right-click): Open, Refresh now, Settings, Start with Windows ✓, Quit.

### Notifications

- Windows toast when a bucket **crosses** the alert threshold (default 90%) — edge-triggered,
  not level-triggered, so one toast per bucket per limit window.
- Re-arms when the bucket's `resets_at` passes or the value drops back below threshold − 5 pp.
- Separate optional toast at 100% ("Session limit reached — resets 10:59").
- Optional "limit reset" toast.
- Per-bucket enable/disable; global quiet mode.

### Flyout window

Borderless, shadowed WPF window, ~280×200 px, positioned adjacent to the tray icon
(taskbar-edge aware: bottom/top/left/right taskbars). Opens on left-click, dismisses on
focus loss / Esc. Light/dark follows Windows theme.

```
┌───────────────────────────────┐
│  Claude usage        ⚙  ↻     │
│                               │
│   ██        ▁▁        ▂▂      │
│   ██        ██        ██      │
│   ██        ██        ██      │
│   14%       11%       16%     │
│  Session   Week      Fable    │
│  ↺ 10:59   ↺ Fri     ↺ Fri    │
│                               │
│  Extra usage: $0 / $127       │
└───────────────────────────────┘
```

Bars are vertical, color-matched to the state thresholds above, with reset countdown under each.

### Settings

`%AppData%\claude-tray\settings.json` (no secrets):
poll interval (10–600 s), alert threshold(s), per-bucket notification toggles,
warn/high color thresholds, start with Windows (HKCU Run key), update-check on/off.

## Distribution

- **Versioning**: semver git tags `vX.Y.Z` drive everything; assembly version via **MinVer**.
- **CI (GitHub Actions)**:
  - `push`/`pull_request` → build + test.
  - tag `v*` → `dotnet publish` (win-x64 + win-arm64, self-contained single file) → **sign** →
    build Inno Setup installer → sign installer → create GitHub Release with installer,
    portable zip, and checksums.
- **Installer**: Inno Setup, per-user (no admin), optional start-at-login, clean uninstall.
- **Code signing / Windows trust**: Authenticode via **Azure Trusted Signing** (cheap,
  CA-backed, integrates with GH Actions OIDC — kills the SmartScreen "unknown publisher" wall).
  Fallback options documented: SignPath OSS (free for open source) or unsigned dev builds.
- **Auto-update**: daily check of GitHub Releases `latest`; toast → opens release page
  (in-place updater later, maybe winget: `winget install claude-tray`).
