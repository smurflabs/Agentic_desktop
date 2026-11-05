# Agentic Desktop

The Windows app for Agentic. It shows your Agentic server's web app, the same pages, look and permissions as in the browser, and opens on **Chat**. It also adds what only a desktop app can do:

- **Claude Code skills and slash commands** from this computer, usable in Chat.
- **Local MCP servers**, as stdio programs or over HTTP, as chat tools.
- **Tools on this computer**: files, commands, and the Agentic API as you. Every change waits for your approval, like Claude Code.

**Setting it up:** [SETUP.md](SETUP.md) covers installing, building from source and connecting to your server.

## How it works

```
WebView2 window ──► http://127.0.0.1:47821 (local server in the app)
                      /desktop/*    desktop layer: desktop.js, desktop.css, the connect screen (www\desktop)
                      /api/local/*  desktop endpoints: settings, skills, local MCP, local tools
                      everything else ──► your Agentic server (the API key is added here, never in the page)
```

The page you see is the server's own web app, so the app always matches the server version. Features such as Chat's **Image** and **Video** buttons appear here as soon as the server has them. Answers (server-sent events) and videos are streamed through the local server, and video byte ranges (`Range`, 206) pass through so a video plays while it loads and can be seeked. The app adds `desktop.js` and `desktop.css` to each page. The title bar follows the web app's light or dark theme:

- **Windows 11:** the bar takes the web app's colours.
- **Windows 10 / Server:** you get the dark or light system bar.

## Setup

1. In the web app, open the user menu → **API key** → **Generate**.
2. Start `AgenticClient.exe` and paste the key. The key is stored encrypted for your Windows account with DPAPI, in `%APPDATA%\AgenticClient\key.bin`.

The app connects to the main Agentic instance, `https://agentic.smurflabs.xyz` (`AppSettings.DefaultServer`). To use another server, switch on **Use a different server** on the connect screen or in Desktop settings; that shows the address box and the **Test** button.

For portable setups, a `config.json` next to the exe can set `serverBaseUrl`, `apiKey` and `port`. A `serverBaseUrl` that differs from the main instance switches the toggle on.

The API key acts with your account's permissions:
- Chat needs **Chat & model API**.
- Tools on the Agentic server (MCP tools, running code there) need **Chat tools & code**.

## Chat on the desktop

Two chips in the message box choose what the model may use on this computer:

- **Skills** opens a panel listing every skill and slash command. You can switch skills on or off for chat, search them, and **Use** one, which puts `/name` in the message.
- **This PC** chooses files, commands and local MCP servers.

| Option | What the model gets |
|---|---|
| Skills | The names and descriptions of the skills that are switched on. It loads a skill's instructions with the `Skill` tool when your request matches, and reads its files with `skill_file`. |
| Files & commands | `read_file`, `list_dir`, `glob_files`, `grep`, `write_file`, `edit_file`, `run_command` (PowerShell or cmd), and `agentic_api` (the Agentic REST API as you). |
| Local MCP servers | Every tool of the chosen connected servers, named `mcp__server__tool`. |

**Run and test the project** (like Claude Desktop's preview): the **Run** chip in the message box (or `/run`; `/run <name>`
starts one, `/run stop [name]`, `/run share [project]`) lists the ways to run each workspace folder:

- `.agentic/launch.json`, or Claude Code's `.claude/launch.json`:
  `{"configurations": [{"name": "Dev server", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], "port": 5173}]}`
  (`command` may replace `runtimeExecutable` + `runtimeArgs`; `kind` is `web`, `app`, `test` or `static`).
- Without one, what the project shows: package.json scripts (with pnpm, yarn or bun when their lock file is there), a
  static site (`index.html`, served by a small built-in server on 127.0.0.1), Django, Flask, FastAPI, pytest, .NET web and
  test projects, Go, Rust and a Makefile.

A web app opens in a **preview pane** right of the chat once its output shows its address (or its port answers): Back,
Reload, developer tools, Open in your browser and Close (the project keeps running; stop it from the chip). Tests and apps
show their output as it comes, with the exit code, **Restart** and **Ask Agentic to fix it**. **Fix how it runs** asks the
model to write `.agentic/launch.json`; **Edit launch.json** opens it. The model has the same as its `preview` tool (status,
start, open, stop, share), and starting or sharing something needs approval in Ask mode. Everything started this way
(background commands and monitors too) stops when the app quits — on Windows also when the app is ended in Task Manager or
crashes. On Linux the page opens in your browser.

**Artifacts** (in the menu under Chat, `/artifacts`, or **All projects and artifacts** in the Run menu): every workspace
folder with its ways to run, and the pages and drawings from your chats (they run in the sandboxed preview). **Share** uploads
a project's website — its build (`dist`, `build`, `out` …) or a static site's folder, without hidden files and
`node_modules` — or a page from a chat, and gives a public link anyone can open; sharing the project again updates the same
link. **Shared links** lists them with their views, **Copy link** and **Stop sharing**.

**Parallel agents.** The model splits independent work into subagents that run at the same time, and reads several files at once (Tool limits → **Parallel agents**, or `/parallel on|off|N`). Each subagent's card shows its progress.

**One window per tool call.** A tool call for this PC goes to one window of the app only (never to two at once), and runs once even if the server sends it again after a reconnect.

**Approvals** follow the chat's **Permission mode**, which you set in the message box:

- **Manual**: Agentic asks before a tool's first use in the chat and before it opens a new site.
- **Auto**: Agentic never stops to ask, except for a local MCP server you set to *Ask first*.

The question appears in every open window: this app, and the web app in a browser. Answer it in whichever is handy: **Allow once**, **Always allow in this chat**, or **Deny**. The taskbar button flashes when the app is in the background.

**Desktop settings → Files & commands on this computer** switches these tools **On** or **Off**.

**Chats in the web app:** while this app is open, the server can reach it. A chat that used this computer's tools keeps them when you continue it in the web app. This app runs the calls; the web app shows the computer's name and shows the answer live, together with its approvals and questions.

In the web app, **Tools → Tools on your computers** lists this app's tools, grouped as files and commands, skills, and each MCP server. You can turn this computer on for any chat there, including a chat started in the browser, and switch single tools off for that chat. An approval or a question answered in one window closes in all the others.

File tools only work inside the **workspace folders**. By default that is your user folder. Commands start in the first workspace folder.

**Slash commands**: type `/` in the message box to pick a skill, a command or one of your **saved prompts**. `/name arguments` runs it:

- A skill's instructions are given to the model with your arguments.
- A command's prompt is used with `$ARGUMENTS` (and `$1`, `$2`, …) filled in.
- A saved prompt replaces the text in the box.

**Screenshots:** the camera chip in the message box, or **Ctrl+Alt+S** from any app. The window steps aside. Drag over the part of the screen you want: Enter or a double-click takes the whole monitor, and Esc cancels. The picture is attached to your next message.

**Shortcuts and the notification area:**

- **Ctrl+Alt+Space** brings Agentic up on a new chat from any app.
- **Monitors**: the model can watch a command on this computer (`monitor`: a log, a dev server, a watch build); its output
  lines go into the chat as they happen, and the server answers them when the chat is not busy.
- **Notifications**: when an answer finishes or a chat needs you while the window is in the background, Windows shows a
  notification from the Agentic icon; clicking it opens that chat. Switch them off with `/notify off` or in the user menu.
- The icon in the notification area opens Agentic, starts a new chat, takes a screenshot or quits.
- With **Keep running in the notification area** on, closing the window only hides it, so shortcuts and live answers keep working.
- Change the shortcuts in Desktop settings → **Shortcuts & window**: click a box and press the keys. A shortcut that another app
  already uses is reported there. The keys of the screenshot layer (whole screen: Enter, cancel: Esc) can be changed there too.
- Shortcuts inside the app (send, new chat, the command palette …) are changed in **Keyboard shortcuts** (user menu, or the button in
  Desktop settings). They are saved to your account, so the browser and VS Code use them too.

**Dictation** (the microphone in the message box) records your voice. The server turns it into text, so OpenWebUI must have speech-to-text set up. The app lets its own pages use the microphone without asking each time (Dictation mode opens it again by itself after each answer). **Read aloud** and **Dictation mode** speak with the server's voices (XTTS), and the speech plays as the server makes it: the app passes every answer from the server on as it arrives (only pages are held to add the desktop layer). The **Audio**, **Images** and **Music** tabs work here as in the web app, songs and speech included. Chat's other features work here as in the web app: versions, projects, memory, sharing and math. See the server README.

## Skills (This computer → Skills)

Skills are loaded from:

| Source | Folder |
|---|---|
| Desktop | `%APPDATA%\AgenticClient\skills\<skill>\SKILL.md` and `…\commands\*.md` (created and edited in the app) |
| Claude Code | `%USERPROFILE%\.claude\skills` and `%USERPROFILE%\.claude\commands` |
| Claude Code plugins | `%USERPROFILE%\.claude\plugins\cache\<marketplace>\<plugin>\<version>\skills` / `commands` (named `plugin:skill`, read-only) |
| More folders | Desktop settings → Skills. Give a skills folder, or a project folder (its `.claude\skills` is used). |

A skill is a folder with `SKILL.md`, in the same format as Claude Code:

```markdown
---
name: release-notes
description: Writes release notes from the git log. Use when the user asks for release notes or a changelog.
allowed-tools: run_command, read_file
---

# Release notes
1. Run `git log --oneline <last-tag>..HEAD` …
```

On the Skills page you can:
- create skills and commands
- edit them
- import a skill `.zip`
- copy a plugin skill into your own skills
- open any skill or command, see its files, and use it in chat

Two sample skills are installed on first start: `standup` and `review-changes`.

## Local MCP servers (This computer → Local MCP)

Servers live in `%APPDATA%\AgenticClient\mcp.json`. A `mcp.json` next to the exe supplies defaults. Both layouts are read:

```json
{ "servers": [ { "id": "files", "name": "Files", "transport": "stdio", "command": "npx",
                 "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Projects"], "requireApproval": false } ] }
```

```json
{ "mcpServers": { "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"], "env": { "GITHUB_TOKEN": "…" } },
                  "remote": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer …" } } } }
```

- **Transports:**
  - `stdio`: a program on this computer. `npx`, `uvx` and other `.cmd` launchers work.
  - `http`: Streamable HTTP, with its session id.
  - `sse`: the older HTTP + SSE transport.
- **Server page:** you can add, edit, switch off, reconnect and remove servers. You can also browse their tools, run a tool with test arguments, and read the server's recent output.
- **Import from Claude:** copies servers you set up in Claude Code (`%USERPROFILE%\.claude.json`, user and project servers) or in Claude Desktop.

## Settings and files

| What | Where |
|---|---|
| Settings | `%APPDATA%\AgenticClient\settings.json` (Desktop settings page) |
| API key | `%APPDATA%\AgenticClient\key.bin` (DPAPI, your Windows account only) |
| MCP servers | `%APPDATA%\AgenticClient\mcp.json` |
| Skills / commands | `%APPDATA%\AgenticClient\skills`, `…\commands` |
| Browser profile | `%APPDATA%\AgenticClient\WebView2` |

**Environment variables** (managed installs, tests):

| Variable | Effect |
|---|---|
| `AGENTIC_CLIENT_SERVER` | Server address |
| `AGENTIC_CLIENT_API_KEY` | API key |
| `AGENTIC_CLIENT_HOME` | Moves the data folder |
| `AGENTIC_CLIENT_DEBUG_PORT` | Opens the WebView2 DevTools port |
| `AGENTIC_CLIENT_BROWSER_ARGS` | Extra Chromium switches for WebView2 (tests: `--use-fake-device-for-media-stream` gives a fake microphone) |

The local server uses port `47821`, so the web app's saved layout, theme and chat model persist. If that port is busy, it picks a free one.

## Security

- The local server listens on 127.0.0.1 only. It refuses requests that come from other web pages (checked with `Origin` / `Sec-Fetch-Site`). That stops a website in your browser from using the app's API key.
- The desktop endpoints (`/api/local/*`) also need a secret that is created at each start and given only to the app's own pages.
- Files and commands run as your Windows account. Keep **Ask first** unless you trust the task, and keep the workspace folders narrow.

## Linux

The same project also builds for Linux: `dotnet publish -r linux-x64` produces `AgenticClient` and `www/desktop`. You can also download `Agentic-Desktop-<version>-linux-x64.tar.gz` from **Apps & updates** and run `tar -xzf` on it.

WinForms and WebView2 exist only on Windows, so on Linux the window is a browser app window. Everything behind the window is the same:
- the local server and its proxy, which adds your API key
- the desktop layer, Skills, Local MCP servers, files and commands
- updates

Starting it:
- `./AgenticClient` opens the window in Chrome, Chromium, Edge, Brave or Vivaldi in app mode (no tabs or address bar). The window uses its own profile, and the app quits when you close it.
- Without one of those browsers, the address opens in your default browser (`xdg-open`). The app then runs until Ctrl+C.
- `AGENTIC_CLIENT_BROWSER` picks the browser.
- `--no-window` runs only the local server, for example on a machine without a desktop. Open the address it prints.

Differences from Windows:
- **API key:** kept in `~/.config/AgenticClient/key`, which only your account can read (Windows uses DPAPI). Settings and data are in `~/.config/AgenticClient`.
- **Commands:** run in bash (or sh).
- **Open folder:** uses `xdg-open`.
- **Screenshots:** use the desktop's own tool, whichever is installed: gnome-screenshot, spectacle, grim with slurp, xfce4-screenshooter, flameshot, scrot or ImageMagick. Without one, the screenshot button is hidden.
- **No shortcuts or tray icon:** global shortcuts and the notification-area icon exist only on Windows, so Desktop settings shows no *Shortcuts & window* card.

## Updates

The app keeps itself up to date from the Agentic server (**Apps & updates** in the web app). The Windows build asks the server for `desktop`, and the Linux build asks for `desktop-linux-x64`. On Linux the new file keeps the old one's file mode, and the restart keeps the app's arguments (such as `--no-window`).

- **When it checks:** at start, before the window loads, and then every few hours.
- **`www\desktop`:** any file that differs from the server's copy is replaced, and files the server no longer has are deleted. When this happens while the app is open, a bar offers to reload the page.
- **The app itself:** when the server has a newer build, the app downloads it in the background and checks it against the server's SHA-256. It then puts the new file in place of `AgenticClient.exe`; the running copy is renamed to `AgenticClient.exe.old-…` and deleted at the next start. A bar offers **Restart now**, and the new version starts when you restart. After a restart, the new version brings `www\desktop` up to date itself.
- **Your settings:** `config.json` and `mcp.json` are never replaced.
- **Turning it off:** Desktop settings → Updates → **Check now**, or switch off *Install updates from the server automatically* (`autoUpdate` in `settings.json`).
- **Development builds:** a build from `bin\Debug` (with `AgenticClient.dll` next to it) never updates itself.

To publish a new version, raise `<Version>` in `AgenticClient.vbproj`, then run `tools\publish-clients.ps1 -Apps desktop` in the Agentic server repository. It builds the Windows and the Linux build. A rebuild with the same version is offered too.

## Build

```
dotnet build
dotnet publish -o publish                         Windows
dotnet publish -r linux-x64 -o publish-linux      Linux (the project builds for net10.0 then; Form1, Desktop\ and Native\ are left out, Linux\ is used)
```

`dotnet publish` makes one self-contained `AgenticClient.exe` (runtime and WebView2 inside, no `.dll` or `.pdb`) next to `www\desktop`.

Needs the WebView2 Runtime, which is built into Windows 11 and current Windows 10. The Agentic server must be a version with the Chat tab, so that local tools appear in Chat.

## License

Agentic Desktop is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It comes WITHOUT ANY WARRANTY. See [LICENSE](LICENSE) for the full text.
