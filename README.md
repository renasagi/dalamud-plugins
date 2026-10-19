# Dalamud Plugins

Add this URL under **Custom Plugin Repositories** in `/xlsettings`:

```text
https://raw.githubusercontent.com/renasagi/dalamud-plugins/main/pluginmaster.json
```

The feed currently contains CritLuck and Party Initials.

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
