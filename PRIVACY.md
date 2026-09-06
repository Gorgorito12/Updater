# Privacy Policy

_Last updated: 2026-07-02_

The **AoE3 Mod Launcher** is a free, open-source desktop application. This
document describes exactly what data it stores, what data leaves your computer,
and how to turn each of those off. It is written to be honest about the code as
it actually behaves — if you find a discrepancy between this policy and what the
launcher does, please [open an issue](https://github.com/Gorgorito12/AoE3-Mod-Launcher/issues).

## Summary (TL;DR)

- **No analytics, no ad networks, no third-party tracking SDKs.** Nothing about
  your usage is sold or shared for advertising.
- **By default the launcher only talks to the internet to check for updates**
  (the launcher itself, mod patches, the mod catalog, translations and news).
  You can turn that off.
- **Multiplayer is opt-in.** Nothing related to multiplayer leaves your computer
  until you choose to sign in with Discord.
- **The local telemetry log is OFF by default** and never leaves your computer
  even when enabled.
- **Sharing your decks is ON by default** — the card names in your home city
  decks (and nothing else about them) go to the lobby server, so the community
  card table has something in it. One switch in Settings stops it, for good.
- **Starting with Windows is asked for, not assumed.** The launcher adds nothing
  to your startup until you say yes on the first launch, and never asks twice.

## What the launcher stores on your computer

These files live in your local app data folder —
**`%LocalAppData%\AoE3ModLauncher\`** — and are **never uploaded anywhere by the
launcher**:

- **`launcher-config.json`** — your settings and, once you sign in to
  multiplayer, the session token issued by the lobby server. Treat this token
  like a password; anyone with the file could act as your multiplayer session
  until it expires.
- **`launcher-debug.log`** — a local diagnostic log (reset on each launch) that
  records what the launcher did, in English, to help debug problems. It stays on
  your machine unless **you** choose to attach it to a bug report.
- **`multiplayer-events.log`** — the optional local telemetry log (see below).
  Off by default.
- **Cached mod assets** (icons, catalog data) under the same folder's
  `mod-assets\` subdirectory.

You can clear caches and temporary files at any time from **Launcher Settings →
Maintenance**.

## Starting with Windows

The launcher can start with Windows and wait in the system tray, so other players see
you as connected and you get a notification when somebody opens a game.

**It asks before setting this up, on your first launch, and writes nothing until you
answer.** Yes is the recommendation on that dialog and nothing more — closing the
window, pressing Escape or clicking the X all count as no. The answer is remembered
either way, so the question is asked exactly once, and once you have declined or turned
it off it never comes back on by itself.

- **What it adds:** one value named `Aoe3ModLauncher` under
  `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`. That is a
  per-user setting; it changes nothing for other accounts on the computer.
- **What it does not add:** no Windows service, no scheduled task, no machine-wide
  registry entry, and no administrator rights. The launcher runs un-elevated.
- **How to undo it:** *Launcher Settings → General → "Start with Windows in the
  background"*, or Windows' own Task Manager → Startup, or by deleting that value.

**What a tray launch does while you are not looking at it:** checks for a launcher
update and a mod update, refreshes the mod catalogue and the translation index, keeps
the multiplayer presence connection open if you are signed in — that is what makes you
appear connected — and asks the lobby server for the room list every 90 seconds so it
can tell you when a game opens. It does not fetch the news feed or re-check card images
until you actually open the window. The update and catalogue traffic is covered by
*Settings → Updates → "Check for updates on startup"*, and the room polling by the
"notify me about new rooms" setting; turning either off stops that half.

## What leaves your computer, and when

### 1. Update, catalog, translation and news checks

On startup (and when you press refresh) the launcher makes ordinary HTTPS
requests to:

- **GitHub** — to check for a newer launcher version, fetch the community mod
  catalog, list translations, and read the news feed.
- **Mod servers** (e.g. `aoe3wol.com`, SourceForge, GitHub Releases) — to read
  version manifests and download mod payloads you ask it to install.
- **The notification feed** (`wol-notify.duckdns.org`, the maintainer's own
  small server) — a single cached request that replaces per-mod GitHub polling
  for the "update available" / "new translation" bell. It serves the same
  public version data; no account or identifier is sent. You can point the
  launcher elsewhere or opt out entirely (always poll GitHub instead) by
  setting `notificationFeedUrl` to `"none"` in `launcher-config.json`.

As with any web request, the remote server sees your IP address. The launcher
sends no personal identifiers in these requests. **You can disable all
startup network activity** — including the notification feed — with
*Launcher Settings → Updates → "Check for updates on startup"*.

### 2. Multiplayer (opt-in — requires Discord sign-in)

Multiplayer is handled by a **self-hosted lobby server** (the maintainer's own
Node.js/Fastify deployment). Nothing below happens unless you open the
Multiplayer tab and sign in.

- **Discord sign-in (OAuth).** When you authorise the app, the lobby server
  receives your Discord **account id, username and avatar** and issues a session
  token that is cached locally in `launcher-config.json`. The launcher does not
  see your Discord password.
- **Lobbies and chat.** When you create or join a room, your display name, the
  room's mod, and your chat messages are sent to the lobby server so other
  players in the room (and, for the global chat, other signed-in users) can see
  them. Global chat history is kept only in the server's memory and is lost when
  the server restarts.
- **Mod fingerprint.** A hash of your installed mod's data files is sent so the
  server can match you only with players on the same mod version. It does not
  identify you or expose your file paths.
- **IP address.** As with any online service, the lobby server sees your IP
  address (used for rate-limiting and basic abuse prevention).

To stop sharing this data, simply do not sign in — or sign out, which clears the
cached session token.

### 3. Teams and tournaments (only if you use them)

If you create or join a **team**, the lobby server stores the team's name, its
optional short tag, and which Discord accounts belong to it. If somebody invites
you to a team, the invitation is stored until you answer it — that is deliberate,
so an invitation sent while you were offline is still there when you come back.

If you enter a **tournament**, the server stores which tournament you entered, the
line-up you entered with, and your results in its bracket.

**This information is public.** A tournament bracket shows the display name and
avatar of everybody in it, the same ones the ranking already shows, and a team page
shows its members. Anyone who can open the launcher's Multiplayer tab can see them.

**What is not stored:** nothing new about your computer or your game. A tournament
match is reported through exactly the same path as any other multiplayer match,
described in section 2.

To stop sharing this, leave the teams you are in and do not enter tournaments.
Disbanding a team keeps the record of tournaments it already played, because those
brackets have to keep showing who took part.

### 4. Home city decks (ON by default)

Unless you turn off **Launcher Settings → Privacy → "Share my decks with the
community table"**, the launcher sends the **card names in your home city
decks**, grouped by civilization, to the same lobby server. This is what fills
the "Cards the community brings" table in **Multiplayer → Statistics**.

**This changed in 1.0.14b.** It used to be off until you turned it on, and it is
now on until you turn it off — including on launchers that were already
installed, which are switched on once when they start. If that is not what you
want, the switch is in the same place it always was and **turning it off is
final**: it is never switched back on for you.

**What is sent:** the internal card names, the civilization each deck belongs to,
the mod, and the Discord account you are signed in with.

**What is NOT sent:** your deck names (those are whatever you typed), which cards
you actually played, any match, and any date of play. The launcher cannot know
which cards you played — the game plays a card by its position in the deck and
never records which one it was, so no such data exists to send.

**How often:** once per session, and only while the switch is on. Each upload
**replaces** what your account sent before, so it is a statement of what you
currently carry rather than a history.

**Turning it off** stops any further upload immediately, and stays off through
every later launch — the one-time switch-on above happens once per computer and
never again. What you already sent stays on the server until you share again
(which replaces it); ask on Discord if you want it removed outright.

Because this is self-reported, it is used **only** for that popularity table and
never for ratings or matchmaking.

### 5. Radmin VPN (third-party, for in-game traffic)

The actual in-game network uses **Radmin VPN by Famatech**, which you install and
manage yourself. The launcher only *assists* (it can detect, help install, and
launch the Radmin client, and copy a network name to your clipboard); it does
**not** bundle Radmin and cannot join a network on your behalf. Your use of
Radmin VPN is governed by **Famatech's own privacy policy and terms**, not this
one.

## Local telemetry log (opt-in, off by default)

The launcher can keep a small local file, `multiplayer-events.log`, with plain
event counters such as "a sign-in was attempted", "a lobby was joined", or "a
rate-limit error occurred". It contains **no message contents and no personal
data**, uses **no network and no third-party service**, and **never leaves your
computer**. Its only purpose is to help you and the maintainer diagnose
multiplayer issues if you choose to share it in a bug report.

This log is **disabled by default**. You can enable or disable it at any time in
**Launcher Settings → Privacy → "Enable local telemetry log"**.

## Third-party services

When you use the relevant features, these third parties may process data under
their own privacy policies:

- **Discord** — sign-in / identity. <https://discord.com/privacy>
- **GitHub** — update, catalog, translation and news hosting.
  <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>
- **Famatech Radmin VPN** — the virtual LAN for in-game traffic.
  <https://www.radmin-vpn.com/>
- **Mod distribution servers** (aoe3wol.com, SourceForge) — mod payload
  downloads, under their respective policies.

## Children

The launcher is not directed at children and does not knowingly collect data
from anyone under the age required to hold a Discord account in their
jurisdiction.

## Changes to this policy

This policy may change as the launcher evolves. Material changes will be
reflected in this file in the repository, with the "Last updated" date above.

## Contact

Questions or concerns: please
[open an issue](https://github.com/Gorgorito12/AoE3-Mod-Launcher/issues) on the project
repository.
