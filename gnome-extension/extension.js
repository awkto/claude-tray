// Claude Usage Tray — GNOME Shell extension.
// Polls Anthropic's OAuth usage endpoint and shows per-bucket limits in the top bar.

import GObject from 'gi://GObject';
import GLib from 'gi://GLib';
import Gio from 'gi://Gio';
import St from 'gi://St';
import Soup from 'gi://Soup';
import Clutter from 'gi://Clutter';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const TOKEN_URL = 'https://console.anthropic.com/v1/oauth/token';
const CLIENT_ID = '9d1c250a-e61b-44d9-88ed-5944d1962f5e'; // Claude Code public OAuth client
const BAR_W = 22;
const BAR_H = 80;

class AuthError extends Error {}

function limitKey(limit) {
    const model = limit.scope?.model?.display_name;
    return model ? `${limit.kind}:${model}` : limit.kind;
}

function limitShortLabel(limit) {
    if (limit.kind === 'session') return 'Session';
    if (limit.kind === 'weekly_all') return 'Week';
    const model = limit.scope?.model?.display_name;
    if (model) return model;
    return limit.kind.replace(/_/g, ' ');
}

function formatReset(iso) {
    const reset = new Date(iso);
    if (Number.isNaN(reset.getTime())) return '';
    const now = new Date();
    const hm = `${String(reset.getHours()).padStart(2, '0')}:${String(reset.getMinutes()).padStart(2, '0')}`;
    if (reset.toDateString() === now.toDateString()) return hm;
    const tomorrow = new Date(now.getTime() + 86400_000);
    if (reset.toDateString() === tomorrow.toDateString()) return `tmrw ${hm}`;
    return `${reset.toLocaleDateString(undefined, {weekday: 'short'})} ${hm}`;
}

const UsageIndicator = GObject.registerClass(
class UsageIndicator extends PanelMenu.Button {
    _init(ext) {
        super._init(0.5, 'Claude Usage');
        this._ext = ext;
        this._settings = ext.getSettings();
        this._session = new Soup.Session({timeout: 30});
        this._cancellable = new Gio.Cancellable();
        this._notifyState = new Map();
        this._snapshot = null;
        this._status = 'Waiting for first poll…';
        this._timeoutId = 0;

        this._icons = {};
        for (const name of ['green', 'orange', 'red', 'gray'])
            this._icons[name] = Gio.icon_new_for_string(`${ext.path}/icons/clawd-${name}.png`);

        const box = new St.BoxLayout();
        this._panelIcon = new St.Icon({gicon: this._icons.gray, icon_size: 18});
        this._panelLabel = new St.Label({
            text: '',
            y_align: Clutter.ActorAlign.CENTER,
            style_class: 'ct-panel-label ct-text-stale',
        });
        box.add_child(this._panelIcon);
        box.add_child(this._panelLabel);
        this.add_child(box);

        this._buildMenu();

        this._settingsChangedId = this._settings.connect('changed', (_s, key) => {
            if (key === 'poll-interval-seconds') this._reschedule();
            if (key === 'show-percent-label') this._updatePanel();
        });
        this.menu.connect('open-state-changed', (_menu, open) => {
            if (open) this._poll();
        });

        this._poll();
        this._reschedule();
    }

    _buildMenu() {
        this._barsItem = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false});
        this._barsBox = new St.BoxLayout({style_class: 'ct-bars', x_expand: true});
        this._barsItem.add_child(this._barsBox);
        this.menu.addMenuItem(this._barsItem);

        this._statusItem = new PopupMenu.PopupBaseMenuItem({reactive: false, can_focus: false});
        this._statusLabel = new St.Label({text: '', style_class: 'ct-status'});
        this._statusLabel.clutter_text.line_wrap = true;
        this._statusItem.add_child(this._statusLabel);
        this.menu.addMenuItem(this._statusItem);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

        const refreshItem = new PopupMenu.PopupMenuItem('Refresh now');
        refreshItem.connect('activate', () => this._poll());
        this.menu.addMenuItem(refreshItem);

        const prefsItem = new PopupMenu.PopupMenuItem('Settings…');
        prefsItem.connect('activate', () => this._ext.openPreferences());
        this.menu.addMenuItem(prefsItem);
    }

    _reschedule() {
        if (this._timeoutId) GLib.source_remove(this._timeoutId);
        const interval = Math.max(10, this._settings.get_int('poll-interval-seconds'));
        this._timeoutId = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, interval, () => {
            this._poll();
            return GLib.SOURCE_CONTINUE;
        });
    }

    // ---- credentials -----------------------------------------------------

    _credentialsPath() {
        const custom = this._settings.get_string('credentials-path');
        return custom || GLib.build_filenamev([GLib.get_home_dir(), '.claude', '.credentials.json']);
    }

    _readCredentials() {
        const path = this._credentialsPath();
        let bytes;
        try {
            [, bytes] = GLib.file_get_contents(path);
        } catch (_e) {
            throw new AuthError(`No credentials at ${path}. Sign in with Claude Code (claude login) or set a path in Settings.`);
        }
        const creds = JSON.parse(new TextDecoder().decode(bytes));
        if (!creds.claudeAiOauth?.accessToken)
            throw new AuthError('Credentials file has no claudeAiOauth.accessToken.');
        return creds;
    }

    _writeCredentials(creds) {
        // GLib.file_set_contents writes via temp file + rename, so Claude Code
        // never sees a half-written file.
        GLib.file_set_contents(this._credentialsPath(), JSON.stringify(creds, null, 2));
    }

    async _getAccessToken() {
        const creds = this._readCredentials();
        const oauth = creds.claudeAiOauth;
        if ((oauth.expiresAt ?? 0) - 60_000 > Date.now())
            return oauth.accessToken;
        if (!oauth.refreshToken)
            throw new AuthError('Token expired and no refresh token available. Run `claude login`.');

        const {status, text} = await this._request('POST', TOKEN_URL, null, JSON.stringify({
            grant_type: 'refresh_token',
            refresh_token: oauth.refreshToken,
            client_id: CLIENT_ID,
        }));
        if (status === 400 || status === 401 || status === 403)
            throw new AuthError('Token refresh rejected. Run `claude login` to sign in again.');
        if (status !== 200)
            throw new Error(`Token endpoint returned ${status}`);

        const resp = JSON.parse(text);
        oauth.accessToken = resp.access_token;
        if (resp.refresh_token) oauth.refreshToken = resp.refresh_token;
        oauth.expiresAt = Date.now() + (resp.expires_in > 0 ? resp.expires_in : 3600) * 1000;
        this._writeCredentials(creds);
        return oauth.accessToken;
    }

    // ---- polling ---------------------------------------------------------

    _request(method, url, token, body = null) {
        return new Promise((resolve, reject) => {
            const msg = Soup.Message.new(method, url);
            if (token) msg.request_headers.append('Authorization', `Bearer ${token}`);
            msg.request_headers.append('anthropic-beta', 'oauth-2025-04-20');
            if (body) {
                msg.set_request_body_from_bytes('application/json',
                    new GLib.Bytes(new TextEncoder().encode(body)));
            }
            this._session.send_and_read_async(msg, GLib.PRIORITY_DEFAULT, this._cancellable, (session, res) => {
                try {
                    const bytes = session.send_and_read_finish(res);
                    resolve({
                        status: msg.get_status(),
                        text: new TextDecoder().decode(bytes.get_data() ?? new Uint8Array()),
                    });
                } catch (e) {
                    reject(e);
                }
            });
        });
    }

    async _poll() {
        if (this._polling) return;
        this._polling = true;
        try {
            const token = await this._getAccessToken();
            const {status, text} = await this._request('GET', USAGE_URL, token);
            if (status === 401)
                throw new AuthError('Usage request unauthorized. Run `claude login`.');
            if (status !== 200)
                throw new Error(`Usage endpoint returned ${status}`);

            const parsed = JSON.parse(text);
            this._snapshot = {
                limits: (parsed.limits ?? []).filter(l => l.percent !== null && l.percent !== undefined),
                extraUsage: parsed.extra_usage,
                fetchedAt: new Date(),
            };
            this._status = null;
            this._processNotifications(this._snapshot);
        } catch (e) {
            if (e instanceof AuthError) {
                this._snapshot = null;
                this._status = e.message;
            } else {
                // Keep the stale snapshot; just surface the problem.
                this._status = this._snapshot
                    ? `Stale — last updated ${this._snapshot.fetchedAt.toLocaleTimeString()}`
                    : `Can't reach Anthropic: ${e.message}`;
            }
        } finally {
            this._polling = false;
        }
        this._updatePanel();
        this._updateMenu();
    }

    // ---- state / rendering -------------------------------------------------

    _classify(percent) {
        if (percent >= this._settings.get_int('alert-threshold')) return 'critical';
        if (percent >= this._settings.get_int('high-threshold')) return 'high';
        if (percent >= 60) return 'warn';
        return 'ok';
    }

    _worst() {
        if (!this._snapshot?.limits.length) return null;
        return this._snapshot.limits.reduce((a, b) => (b.percent > a.percent ? b : a));
    }

    _updatePanel() {
        const worst = this._worst();
        let icon = 'gray';
        let textClass = 'ct-text-stale';
        let labelText = '';
        if (worst) {
            const state = this._classify(worst.percent);
            icon = state === 'critical' ? 'red' : state === 'high' ? 'orange' : 'green';
            textClass = state === 'critical' ? 'ct-text-critical' : state === 'high' ? 'ct-text-high' : 'ct-text-ok';
            labelText = `${Math.round(worst.percent)}%`;
        }
        this._panelIcon.gicon = this._icons[icon];
        this._panelLabel.style_class = `ct-panel-label ${textClass}`;
        this._panelLabel.text = this._settings.get_boolean('show-percent-label') ? labelText : '';
        this._panelLabel.visible = this._panelLabel.text !== '';
    }

    _updateMenu() {
        this._barsBox.destroy_all_children();
        const limits = this._snapshot?.limits ?? [];

        for (const limit of limits) {
            const state = this._classify(limit.percent);
            const column = new St.BoxLayout({vertical: true, style_class: 'ct-bar-column'});

            const track = new St.Widget({style_class: 'ct-bar-track', width: BAR_W, height: BAR_H});
            const fill = new St.Widget({style_class: `ct-bar-fill ct-fill-${state}`});
            const h = Math.max(3, Math.round(BAR_H * Math.min(limit.percent, 100) / 100));
            fill.set_size(BAR_W, h);
            fill.set_position(0, BAR_H - h);
            track.add_child(fill);

            const trackBin = new St.Bin({child: track, x_align: Clutter.ActorAlign.CENTER});
            column.add_child(trackBin);
            column.add_child(new St.Label({
                text: `${Math.round(limit.percent)}%`,
                style_class: `ct-bar-percent ct-text-${state === 'warn' ? 'ok' : state}`,
            }));
            column.add_child(new St.Label({text: limitShortLabel(limit), style_class: 'ct-bar-name'}));
            if (limit.resets_at)
                column.add_child(new St.Label({text: `↺ ${formatReset(limit.resets_at)}`, style_class: 'ct-bar-reset'}));

            this._barsBox.add_child(column);
        }
        this._barsItem.visible = limits.length > 0;

        let statusText = this._status ?? '';
        const extra = this._snapshot?.extraUsage;
        if (extra?.is_enabled && extra.monthly_limit != null) {
            const div = 10 ** (extra.decimal_places ?? 2);
            const line = `Extra usage: ${(extra.used_credits ?? 0) / div} / ${extra.monthly_limit / div} ${extra.currency ?? ''}`;
            statusText = statusText ? `${line}\n${statusText}` : line;
        }
        this._statusLabel.text = statusText;
        this._statusItem.visible = statusText !== '';
    }

    // ---- notifications -----------------------------------------------------

    _processNotifications(snapshot) {
        const alertAt = this._settings.get_int('alert-threshold');
        const muted = this._settings.get_boolean('mute-all');

        for (const limit of snapshot.limits) {
            const key = limitKey(limit);
            const st = this._notifyState.get(key) ?? {windowId: null, thresholdAlerted: false, exceededAlerted: false};
            this._notifyState.set(key, st);
            const pct = limit.percent;
            const resetsAt = limit.resets_at ? Date.parse(limit.resets_at) : null;

            if (st.windowId && resetsAt && resetsAt > st.windowId) {
                const wasAlerted = st.thresholdAlerted;
                st.thresholdAlerted = false;
                st.exceededAlerted = false;
                if (!muted && wasAlerted && this._settings.get_boolean('notify-reset'))
                    Main.notify(`Claude: ${limitShortLabel(limit)} limit reset`, `Usage window restarted — now ${Math.round(pct)}%.`);
            }
            if (resetsAt) st.windowId = resetsAt;

            if (st.thresholdAlerted && pct < alertAt - 5) st.thresholdAlerted = false;
            if (st.exceededAlerted && pct < 95) st.exceededAlerted = false;

            if (muted) continue;
            const suffix = limit.resets_at ? ` Resets ${formatReset(limit.resets_at)}.` : '';
            if (pct >= 100 && !st.exceededAlerted && this._settings.get_boolean('notify-exceeded')) {
                st.exceededAlerted = true;
                st.thresholdAlerted = true;
                Main.notify(`Claude: ${limitShortLabel(limit)} limit reached`, `You've hit 100% of this limit.${suffix}`);
            } else if (pct >= alertAt && !st.thresholdAlerted && this._settings.get_boolean('notify-threshold')) {
                st.thresholdAlerted = true;
                Main.notify(`Claude: ${limitShortLabel(limit)} at ${Math.round(pct)}%`, `Approaching your limit (alert set at ${alertAt}%).${suffix}`);
            }
        }
    }

    destroy() {
        if (this._timeoutId) {
            GLib.source_remove(this._timeoutId);
            this._timeoutId = 0;
        }
        if (this._settingsChangedId) {
            this._settings.disconnect(this._settingsChangedId);
            this._settingsChangedId = 0;
        }
        this._cancellable.cancel();
        this._session.abort();
        super.destroy();
    }
});

export default class ClaudeTrayExtension extends Extension {
    enable() {
        this._indicator = new UsageIndicator(this);
        Main.panel.addToStatusArea(this.uuid, this._indicator);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
