import Gio from 'gi://Gio';
import Gtk from 'gi://Gtk';
import Adw from 'gi://Adw';

import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

export default class ClaudeTrayPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();
        const page = new Adw.PreferencesPage();
        window.add(page);

        const alerts = new Adw.PreferencesGroup({title: 'Alerts'});
        page.add(alerts);

        const threshold = new Adw.SpinRow({
            title: 'Alert threshold',
            subtitle: 'Notify and turn red when any limit reaches this percentage',
            adjustment: newAdjustment(settings.get_int('alert-threshold'), 50, 99),
        });
        settings.bind('alert-threshold', threshold, 'value', Gio.SettingsBindFlags.DEFAULT);
        alerts.add(threshold);

        const high = new Adw.SpinRow({
            title: 'Orange threshold',
            subtitle: 'Indicator turns orange when any limit reaches this percentage',
            adjustment: newAdjustment(settings.get_int('high-threshold'), 30, 98),
        });
        settings.bind('high-threshold', high, 'value', Gio.SettingsBindFlags.DEFAULT);
        alerts.add(high);

        addSwitch(alerts, settings, 'notify-threshold', 'Notify at alert threshold');
        addSwitch(alerts, settings, 'notify-exceeded', 'Notify when a limit reaches 100%');
        addSwitch(alerts, settings, 'notify-reset', 'Notify when a limit window resets');
        addSwitch(alerts, settings, 'mute-all', 'Mute all notifications');

        const general = new Adw.PreferencesGroup({title: 'General'});
        page.add(general);

        const interval = new Adw.SpinRow({
            title: 'Poll interval (seconds)',
            adjustment: newAdjustment(settings.get_int('poll-interval-seconds'), 10, 600),
        });
        settings.bind('poll-interval-seconds', interval, 'value', Gio.SettingsBindFlags.DEFAULT);
        general.add(interval);

        addSwitch(general, settings, 'show-percent-label', 'Show percentage in the top bar');

        const creds = new Adw.EntryRow({
            title: 'Credentials file (empty = ~/.claude/.credentials.json)',
        });
        settings.bind('credentials-path', creds, 'text', Gio.SettingsBindFlags.DEFAULT);
        general.add(creds);
    }
}

function newAdjustment(value, lower, upper) {
    return new Gtk.Adjustment({value, lower, upper, step_increment: 1, page_increment: 10});
}

function addSwitch(group, settings, key, title) {
    const row = new Adw.SwitchRow({title});
    settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(row);
}
