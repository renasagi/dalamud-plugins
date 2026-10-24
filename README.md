# Dalamud Plugins

Add this URL under **Custom Plugin Repositories** in `/xlsettings`:

```text
https://raw.githubusercontent.com/renasagi/dalamud-plugins/main/pluginmaster.json
```

The feed contains CritLuck, Party Initials, and Client Language Override.

Download counts use GitHub release totals and refresh every six hours.
Repeat downloads and updates count too.

## CritLuck

The ACT testing build has been withdrawn because it does not support all jobs.
Stable **1.0.0.0** remains available and does not include the ACT overhaul.

If you installed **2.0.0.0**, leave CritLuck's testing channel and reinstall
stable from `/xlplugins`.

## Client Language Override

Choose the client language shown to other players without changing your game's
text language. Open `/clientlang` to select Japanese, English, German, or French.

Enable **Get plugin testing builds** in Dalamud settings, then install
**Client Language Override** from `/xlplugins`.

Language override supports FFXIV Windows build `2026.10.16.0000.0000` and
requires Dalamud API 15. On other game builds, the plugin opens an update
warning and leaves language override inactive. Saved settings are kept.
If Search Info still shows the previous language, restart the game.

## Party Initials (deprecated)

Use [Vanilla+](https://github.com/MidoriKami/VanillaPlus)'s **Game Config Command**
feature instead, contributed by [Ren (renasagi)](https://github.com/MidoriKami/VanillaPlus/pull/332).
Install Vanilla+ and enable the feature in its settings. For initials in both
the party list and chat, run these commands outside combat:

```text
/gameconfig control PartyListNameType 3
/gameconfig ui LogNameType 3
```

Use `0` for full names, `1` for `Forename S.`, `2` for `F. Surname`, or `3` for
`F. S.`. Use the same value in both commands. Replace `/pli` commands in macros
with the two corresponding `/gameconfig` lines.

Party Initials remains in this feed and is still
[available to download](https://github.com/renasagi/dalamud-plugins/releases/download/partyinitials-v1.1.1/PartyInitials.zip).
Its existing commands still work; you can remove it once you have switched.
