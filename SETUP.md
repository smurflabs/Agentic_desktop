# Setting up Agentic Desktop

Agentic Desktop shows your Agentic server's web app in a window of its own, opens on **Chat**, and adds what only a desktop app can do:
- Claude Code skills and slash commands from this computer
- local MCP servers
- tools that work on this computer's files and commands, with your approval

The [reference manual](docs/REFERENCE.md) covers the features in depth.

## 1. What you need

- **A running Agentic server.** See the Agentic repository's `SETUP.md`.
- **An account on that server:**
  - **Chat & model API** for Chat.
  - **Chat tools & code** as well, to use the server's tools from Chat.
- **A personal API key.** In the web app, open the user menu → **API key** → **Generate**.
- **Windows:** Windows 10 version 2004 (build 19041) or later, or Windows 11, with the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). WebView2 is built into Windows 11 and current Windows 10.
- **Linux (x64, ARM64) and macOS (Apple silicon, Intel):** Chrome, Chromium, Edge, Brave or Vivaldi for the app window. Without one, the default browser is used.
- **To build from source:** the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Published builds are self-contained.

## 2. Install

### From your server (no build)

When the server's administrator has published the desktop app (Agentic `SETUP.md`, section 9), open **Apps & updates** in the web app and download it:
- **Windows:** a zip. Extract it into a folder of its own and run `AgenticClient.exe`.
- **Linux:** a `.tar.gz`. Run `tar -xzf Agentic-Desktop-<version>-linux-x64.tar.gz`, then `./Agentic-Desktop/AgenticClient`.

### From source

```powershell
git clone <this repository> Agentic_desktop
cd Agentic_desktop
dotnet build                                      # Windows development build: bin\Debug\...\AgenticClient.exe
dotnet publish -o publish                         # Windows: one self-contained AgenticClient.exe plus www\desktop
dotnet publish -r linux-x64 -o publish-linux      # Linux: AgenticClient plus www/desktop
```

Keep the `www\desktop` folder next to the program: it holds the desktop layer (the connect screen, `desktop.js`, `desktop.css`).

The project builds the Windows app (WinForms and WebView2) by default. With `-r linux-x64` it builds for plain `net10.0`: `Form1.vb`, `Desktop\` and `Native\` are left out, and `Linux\` is used. On a Linux machine, always pass `-r linux-x64`.

## 3. Connect to your server

1. Start the app. The connect screen opens.
2. Switch on **Use a different server** and type your server's address, for example `https://agentic.example.com` or `http://127.0.0.1:8080`. The app has a built-in default address. **Test** checks your address.
3. Paste your API key.

Where the key is kept:
- **Windows:** encrypted for your Windows account (DPAPI) in `%APPDATA%\AgenticClient\key.bin`.
- **Linux:** in `~/.config/AgenticClient/key`, which only your account can read.

**Portable or managed installs:**
- A `config.json` next to the program can set `serverBaseUrl`, `apiKey` and `port`. It holds the key as plain text, so this repository's `.gitignore` excludes it.
- Environment variables also work:
  - `AGENTIC_CLIENT_SERVER`: the server address.
  - `AGENTIC_CLIENT_API_KEY`: the API key.
  - `AGENTIC_CLIENT_HOME`: moves the data folder.

## 4. Using it

- **Chat** works as in the browser. **This computer** adds Skills, Local MCP servers, and the files and commands tools.
- Local tools run as your account. Keep **Ask first** on unless you trust the task, and keep the workspace folders narrow.
- The app runs a local server on `127.0.0.1:47821` (or another free port when that one is busy). The server adds your API key to each request, so the page never sees the key.

On Linux:
- `./AgenticClient --no-window` runs only the local server. Open the address it prints.
- `AGENTIC_CLIENT_BROWSER` picks the browser for the window.

## 5. Where things are

| What | Windows | Linux |
|---|---|---|
| Settings | `%APPDATA%\AgenticClient\settings.json` | `~/.config/AgenticClient/settings.json` |
| MCP servers | `%APPDATA%\AgenticClient\mcp.json` (a `mcp.json` next to the program supplies defaults) | `~/.config/AgenticClient/mcp.json` |
| Skills and commands | `%APPDATA%\AgenticClient\skills`, `…\commands` | `~/.config/AgenticClient/skills`, `…/commands` |

## 6. Updates

The app checks your server at start and every few hours. It downloads a newer build in the background, checks it against the server's SHA-256, and offers **Restart now**.
- To turn this off, use Desktop settings → Updates (`autoUpdate` in `settings.json`).
- A development build from `bin\Debug` never updates itself.

To release a new version from source:
1. Raise `<Version>` in `AgenticClient.vbproj`.
2. Run `.\tools\publish-clients.ps1 -Apps desktop` in the Agentic repository, with this repository cloned next to it.

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| Blank window on Windows | Install the WebView2 Runtime. |
| **Test** fails on the connect screen | Check the address (scheme and port) and that the server answers `/healthz`. |
| 401 after connecting | Generate a new API key and paste it in Desktop settings. |
| Chat tools missing | Your account lacks **Chat tools & code**, or the server is too old for local tools. |
| No screenshot button on Linux | Install one of gnome-screenshot, spectacle, grim and slurp, xfce4-screenshooter, flameshot, scrot or ImageMagick. |

## License

Agentic Desktop is free software, licensed under the GNU General Public License v3.0 or later. See [LICENSE](LICENSE).
