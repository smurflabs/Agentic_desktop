<div align="center">

<img src="www/desktop/logo.png" alt="Agentic" width="288">

**Agentic Desktop: your Agentic server in a desktop app, with tools that work on your own computer.**

[![Release](https://github.com/smurflabs/Agentic_desktop/actions/workflows/release.yml/badge.svg)](https://github.com/smurflabs/Agentic_desktop/releases)
[![License: GPL v3](https://img.shields.io/badge/license-GPL--3.0--or--later-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Runs on Windows, Linux and macOS](https://img.shields.io/badge/runs%20on-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)

[Install](#install) · [Screenshots](#screenshots) · [Features](#features) · [MCP servers](#mcp-servers) · [Setup guide](SETUP.md) · [Reference manual](docs/REFERENCE.md) · [Agentic server](https://github.com/smurflabs/Agentic)

</div>

---

## What is it?

Agentic Desktop shows your [Agentic server](https://github.com/smurflabs/Agentic)'s web app in a window of its own and opens on **Chat**. You get the same pages, look and permissions as in the browser, always in step with the server's version.

It also adds what only a desktop app can do: the model can work with the files, commands, Claude Code skills and MCP servers on your computer, and every change waits for your approval.

```text
WebView2 window ──► local server on 127.0.0.1
                      ├─ desktop layer and local tools (files, commands, skills, MCP)
                      └─ everything else ──► your Agentic server (your API key is added here, never in the page)
```

## Screenshots

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/screenshots/desktop-home.png" alt="Agentic Desktop on Windows: the web app with This computer: Skills, Local MCP and Desktop settings"><br>
      <sub><b>The whole web app</b> in its own window, plus <b>This computer</b>: Skills, local MCP servers and Desktop settings.</sub>
    </td>
    <td width="50%" valign="top">
      <img src="docs/screenshots/desktop-chat.png" alt="Agentic Desktop chat with the Skills and This PC chips and the tokens-per-second gauge"><br>
      <sub><b>Chat</b>: skills and this computer's tools are chips in the message box, and the model bar shows live speed.</sub>
    </td>
  </tr>
  <tr>
    <td colspan="2" valign="top">
      <img src="docs/screenshots/desktop-multitask.png" alt="Agentic Desktop Multitask: two chats side by side"><br>
      <sub><b>Multitask</b>: up to four chats side by side, each answering live.</sub>
    </td>
  </tr>
</table>

## Features

### Tools on this computer

- **Files and commands:**
  - read, search, write and edit files in the workspace folders you choose
  - run PowerShell or cmd commands (bash on Linux)
  - call the Agentic API as you
- **Approvals:** changes wait for you, following the chat's permission mode. You can answer **Allow once**, **Always allow in this chat** or **Deny** in any open window.
- **Use from other windows:** while the app is open, chats started in the web app can use this computer's tools too.
- **Parallel agents:** the model splits independent work into subagents that run at the same time.
- **Monitors:** the model can watch a log, a dev server or a build, and react to new output.

### Skills and MCP

- **Claude Code skills and slash commands** from `~/.claude`, from Claude Code plugins and from the app's own library. You can create, edit, import and use them in chat.
- **Local MCP servers** over stdio, Streamable HTTP or SSE. You can import them from Claude Code, Claude Desktop, VS Code and Visual Studio, browse and test their tools, and set them to *Ask first*.

### Run, preview and share

- **Run and test projects** from the **Run** chip:
  - `.agentic/launch.json` or `.claude/launch.json`
  - otherwise detected from the project: npm scripts, static sites, Django, Flask, FastAPI, pytest, .NET, Go, Rust, Makefiles
- **Preview pane:** a web app opens beside the chat, with developer tools. Tests show their exit code and an **Ask Agentic to fix it** button.
- **Artifacts:** projects and chat pages can be shared as public links.

### A real desktop app

- **Global shortcuts:** **Ctrl+Alt+Space** opens a new chat from any app, and **Ctrl+Alt+S** takes a screenshot of part of the screen into the chat.
- **Notification-area icon** and Windows notifications when an answer finishes or a chat needs you.
- **Voice:** dictation, Dictation mode and answers read aloud with the server's voices.
- **Every tab of the web app,** including Audio, Images and Music.
- **Self-updating:** the app and its desktop layer update themselves from your Agentic server.
- **Look:** the title bar follows the web app's light or dark theme.

## MCP servers

Chat in the desktop app gets [MCP](https://modelcontextprotocol.io) tools from two sides: the Agentic server, and this computer.

### Ready without any setup

- **Your Agentic server's MCP servers.** These are the server's [16 built-in tool servers](https://github.com/smurflabs/Agentic#built-in-tool-servers) (storage, git, documents, database, data, files, code review, network checks, utilities and more), plus the servers you or your administrator added under **My MCP servers**. Turn them on in Chat's **Tools** menu. They run on the server, and you can use only the ones your account may use.
- **Tools on this computer.** These are built into the app (not MCP): read, search, write and edit files in your workspace folders, run commands, and call the Agentic API as you. Turn them on with the **This PC** chip.
- **REA (reverse engineering)** is added to your list switched off, ready to turn on once Node.js is installed.

### The catalog: well-known servers in one click

Open **This computer → Local MCP** and choose a server from the catalog. Servers that need nothing else are added in one click; for the others, a filled-in form asks for the folder or API key. The same list is in the web app, VS Code and the CLI.

| Server | What it does | Needs |
|---|---|---|
| Filesystem | Read, search and edit files in the folders you allow | Node.js |
| Git | Read a Git repository: status, diffs, log, branches and commits | uv (Python) |
| GitHub | GitHub's official server: repositories, issues, pull requests, code search and Actions (hosted) | A GitHub token |
| Desktop Commander | Terminal commands, processes and file editing on the computer it runs on | Node.js |
| Playwright browser | Microsoft's browser automation: open pages, click, type, fill forms and read pages | Node.js 20+ |
| Chrome DevTools | Google's server for Chrome: drive the browser, read the console and network, record performance traces | Node.js 20.19+ and Chrome |
| Fetch | Download a web page and turn it into Markdown | uv (Python) |
| Firecrawl | Scrape, crawl and extract structured data from websites, including JavaScript-heavy ones | Node.js 22+ and a Firecrawl API key |
| Brave Search | Web, news, image and local search through Brave's own index | Node.js and a Brave Search API key |
| Tavily | Search built for AI agents, with page extraction and site crawling | Node.js and a Tavily API key |
| Exa | Neural web search: pages by meaning, research papers, companies and code | Node.js 20+ and an Exa API key |
| Context7 | Current documentation and code examples for thousands of libraries | Node.js 20+ |
| Microsoft Learn | Search and read Microsoft's documentation: .NET, Azure, Windows, Microsoft 365 (hosted) | Nothing |
| DeepWiki | Ask questions about any public GitHub repository and read its generated wiki (hosted) | Nothing |
| AWS Documentation | Search and read AWS documentation | uv (Python) |
| Notion | Search, read and update pages and databases in your Notion workspace | Node.js and a Notion integration token |
| Supabase | Query and manage Supabase projects: tables, SQL, migrations and logs (read-only by default) | Node.js and a Supabase access token |
| Memory | A knowledge graph the model stores facts in and recalls across sessions | Node.js |
| Sequential Thinking | Structured step-by-step reasoning that the model can revise and branch | Node.js |
| Time | The current time in any time zone, and conversions | uv (Python) |
| REA | Reverse engineering of native binaries, .NET assemblies, JS/Electron apps, Android APKs and websites | Node.js 22+; optionally Ghidra, Hopper or IDA |

### Add your own

Open **This computer → Local MCP** and choose **Add server**:

- **Transports:**
  - **stdio:** a program on this computer. `npx`, `uvx` and other `.cmd` launchers work.
  - **HTTP:** Streamable HTTP.
  - **SSE:** the older HTTP + SSE transport.
- **Options:** environment variables, headers, a timeout, and **Ask first**, which asks you before each of the server's tools is used, in every permission mode.
- **Import** copies servers you set up in Claude Code, Claude Desktop, Visual Studio or VS Code. It searches your workspace folders too.
- **Install on my computer:** on the web app's **My MCP servers** page, this sends a server to this app, and its form opens filled in.
- **Sign in:** hosted servers that use OAuth sign in through the browser.
- **The server page:** switch servers on and off, reconnect them, browse their tools, run a tool with test arguments, and read a server's recent output.

The servers are kept in `%APPDATA%\AgenticClient\mcp.json` (`~/.config/AgenticClient/mcp.json` on Linux and macOS). The file can also be edited by hand, in this app's layout or Claude Code's:

```json
{ "mcpServers": {
    "files":  { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Projects"] },
    "remote": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer …" } } } }
```

While the app is open, chats in the web app can use these servers too: turn on this computer in **Tools → Tools on your computers**. Every setting is in the [reference manual](docs/REFERENCE.md#local-mcp-servers-this-computer--local-mcp).

## Install

### From a release

Download the build for your system from [Releases](https://github.com/smurflabs/Agentic_desktop/releases):

| File | System |
|---|---|
| `AgenticDesktop-<version>-x64.msi` | **Windows installer.** Installs for your account only, with no administrator rights, and adds Start menu and desktop shortcuts. |
| `AgenticClient.exe` | Windows, portable: one self-contained file. Run it from any folder you can write to. |
| `agentic-desktop-<version>-linux-x64.tar.gz` | Linux x64 |
| `agentic-desktop-<version>-linux-arm64.tar.gz` | Linux ARM64 |
| `agentic-desktop-<version>-osx-arm64.tar.gz` | macOS on Apple silicon (M1 and later) |
| `agentic-desktop-<version>-osx-x64.tar.gz` | macOS on Intel |

On Windows you need Windows 10 (version 2004) or later, x64, with the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which Windows 11 already has. On Linux and macOS, extract the archive and run `./Agentic-Desktop/AgenticClient`. The window opens in Chrome, Chromium, Edge, Brave or Vivaldi.

### From your Agentic server

When your server's administrator has published the desktop app, download it from **Apps & updates** in the web app.

### From source

```powershell
dotnet publish -o publish                         # Windows
dotnet publish -r linux-x64 -o publish-linux      # Linux
```

This needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Keep the `www` folder next to the program.

## Connect

1. In the Agentic web app, open the user menu, then **API key** → **Generate**.
2. Start the app and switch on **Use a different server**.
3. Type your server's address, then paste the key.

On Windows the key is stored encrypted for your account (DPAPI). On Linux it is stored in `~/.config/AgenticClient/key`, which only you can read.

## Documentation

- **[SETUP.md](SETUP.md):** requirements, installing, connecting, settings files and troubleshooting.
- **[docs/REFERENCE.md](docs/REFERENCE.md):** the complete manual, covering chat tools, Run and preview, skills, local MCP servers, shortcuts, Linux, updates and security.

## Building

```powershell
dotnet build
```

The app is written in VB.NET on .NET 10. On Windows it uses WinForms and WebView2. On Linux it builds for plain `net10.0` and opens a browser app window, with the same local server behind it.

## License

Agentic Desktop is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License](LICENSE) as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
