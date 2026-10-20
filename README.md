# Dalamud Plugins

Add this URL under **Custom Plugin Repositories** in `/xlsettings`:

```text
https://raw.githubusercontent.com/renasagi/dalamud-plugins/main/pluginmaster.json
```

The feed contains CritLuck, Party Initials, and Client Language Override.

## CritLuck (ACT testing build)

Version **2.0.0.0** uses regular ACT through OverlayPlugin. Stable **1.0.0.0**
is unchanged and does not contain this ACT overhaul.

1. Add the repository above. In `/xlsettings`, open **Experimental**, enable
   **Get plugin testing builds**, and save.
2. In `/xlplugins`, find **CritLuck**, use its context menu to select the testing
   version, and install or update. Confirm version **2.0.0.0**. Dalamud staging
   is not required.
3. Run ACT with the FFXIV parsing plugin and OverlayPlugin on the same Windows
   PC as the game. Confirm that ACT itself sees combat data.
4. Start OverlayPlugin's **WSServer** on `127.0.0.1`, port `10501`, with SSL off.
   Keep it on loopback; no public listener or router port forwarding is needed.
5. Outside combat, open `/critluck`, expand **ACT connection and scoring**, match
   the port, and click **Apply**. Wait for **ACT: connected**.
6. Enable **Score ordinary level-100 PvE**, give party inspections time to finish,
   and start a new fight. Scoring is off by default.

CritLuck uses `ws://127.0.0.1:10501/ws`, not `/MiniParse`. No IINACT or FFLogs
account is needed. Do not import logs while collecting a live encounter.

Direct-action rules cover **WAR, WHM, SAM, DNC, and PCT at effective level 100**.
Other actions, pets, DoTs, missing stats/status context, and ambiguous buff
boundaries are excluded. Leave scoring off in PvP or special content with
additional stat/rate modifiers. Requires Dalamud API 15.

Around 50 is typical; higher is luckier and lower is less lucky. Scores are not
DPS or damage-weighted rankings. Hover a player or score for details. A dash
means no usable score, not zero luck. Reconnect outside combat if the window
reports a clock, malformed-event, or ordering problem, then start a new fight.

Local automated checks pass; actual ACT/FFXIV compatibility, inspection timing,
and the in-game UI still need validation. To return to stable, leave CritLuck's
testing channel and reinstall stable if needed. Redact names/worlds in reports
and do not post raw ACT logs publicly.

See the [testing release and full instructions](https://github.com/renasagi/dalamud-plugins/releases/tag/critluck-v2.0.0-testing.1).

## Client Language Override

Choose the client language shown to other players without changing your game's
text language. Open `/clientlang` to select Japanese, English, German, or French.

Enable **Get plugin testing builds** in Dalamud settings, then install
**Client Language Override** from `/xlplugins`.

Requires FFXIV Windows build `2026.10.06.0000.0000` and Dalamud API 15.
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
