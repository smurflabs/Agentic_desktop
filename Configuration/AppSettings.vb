Imports System.IO
Imports System.Text.Json

''' <summary>
''' Local client settings: %APPDATA%\AgenticClient\settings.json, an optional config.json next to the exe (portable
''' setups), the DPAPI-protected API key, and environment overrides (AGENTIC_CLIENT_SERVER, AGENTIC_CLIENT_API_KEY).
''' </summary>
Public Class AppSettings

    ''' <summary>The main Agentic instance; used unless "Use a different server" is switched on.</summary>
    Public Const DefaultServer As String = "https://agentic.smurflabs.xyz"
    ''' <summary>Fixed so the web app's per-origin storage (theme, chat model, layout) survives restarts.</summary>
    Public Const DefaultPort As Integer = 47821

    ''' <summary>Only used when UseCustomServer is on.</summary>
    Public Property ServerBaseUrl As String = DefaultServer
    Public Property UseCustomServer As Boolean
    Public Property ApiKey As String = ""
    ''' <summary>This app's id for the Agentic server (chats started elsewhere can use this computer's tools through it).</summary>
    Public Property DeviceId As String
    ''' <summary>Page opened at start; the desktop app opens Chat first.</summary>
    Public Property StartPage As String = "/chat"
    Public Property Port As Integer = DefaultPort
    ''' <summary>Last theme the web app showed ("dark" or "light"); paints the title bar before the page loads.</summary>
    Public Property LastTheme As String = "dark"
    Public Property ExtraHeaders As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>Install new builds from the Agentic server automatically (the exe and www\desktop).</summary>
    Public Property AutoUpdate As Boolean = True

    ' ---- shortcuts and the window
    ''' <summary>Shortcut that brings the app up on a new chat from any other app ("" = none).</summary>
    Public Property QuickEntryHotkey As String = "Ctrl+Alt+Space"
    ''' <summary>Shortcut that captures part of the screen into the chat ("" = none).</summary>
    Public Property ScreenshotHotkey As String = "Ctrl+Alt+S"
    ''' <summary>While choosing the area of a screenshot: the key that takes the whole monitor under the mouse.</summary>
    Public Property ScreenshotWholeScreenKey As String = "Enter"
    ''' <summary>While choosing the area of a screenshot: the key that cancels it.</summary>
    Public Property ScreenshotCancelKey As String = "Esc"
    ''' <summary>Closing the window keeps the app in the notification area, so shortcuts and live answers keep working.</summary>
    Public Property CloseToTray As Boolean
    ''' <summary>The model in Chat may take screenshots of this computer's screen (take_screenshot; the user approves each one).</summary>
    Public Property ModelScreenshots As Boolean = True
    ''' <summary>The model may use the mouse, keyboard, windows and clipboard (computer use); off by default, every action asks.</summary>
    Public Property ComputerUse As Boolean
    ''' <summary>Logs each answer's tokens in Claude Code's format, for npx ccusage (UsageLog).</summary>
    Public Property CcusageLog As Boolean = True

    ' ---- skills and slash commands
    ''' <summary>Also load Claude Code's personal skills and commands (%USERPROFILE%\.claude\skills, \commands).</summary>
    Public Property IncludeClaudeCodeSkills As Boolean = True
    ''' <summary>Also load skills and commands of installed Claude Code plugins (%USERPROFILE%\.claude\plugins).</summary>
    Public Property IncludeClaudePlugins As Boolean = True
    ''' <summary>More folders: a skills folder (skill sub-folders with SKILL.md) or a project (its .claude\skills is used).</summary>
    Public Property SkillFolders As New List(Of String)

    ' ---- tools on this computer (files and commands)
    ''' <summary>off | ask (every change and command needs your approval) | auto (no questions).</summary>
    Public Property LocalToolsMode As String = "ask"
    ''' <summary>Folders the file tools may use; commands start in the first one.</summary>
    Public Property WorkspaceFolders As New List(Of String)
    ''' <summary>powershell | cmd on Windows; bash | sh on Linux.</summary>
    Public Property Shell As String = LocalTools.Shells()(0)
    Public Property CommandTimeoutSeconds As Integer = 120
    ''' <summary>Also run the hooks in Claude Code's own ~/.claude/settings.json (off by default: they are Claude Code's).</summary>
    Public Property IncludeClaudeUserHooks As Boolean
    ''' <summary>Use the MCP servers set up in Claude Code (~/.claude.json, its plugins) and Claude Desktop as they are (no import).</summary>
    Public Property IncludeClaudeMcp As Boolean = True
    ''' <summary>The tools may use files anywhere on this computer (off: outside the workspace folders asks first).</summary>
    Public Property WholeComputer As Boolean

    Public Function NormalizedBaseUrl() As String
        If Not UseCustomServer Then Return DefaultServer
        Dim url = If(ServerBaseUrl, "").Trim()
        If url.Length = 0 Then url = DefaultServer
        If Not url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) AndAlso
           Not url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then url = "http://" & url
        Return url.TrimEnd("/"c)
    End Function

    Public Function HasKey() As Boolean
        Return Not String.IsNullOrWhiteSpace(ApiKey)
    End Function

    Public Function EffectiveWorkspaces() As List(Of String)
        Dim list = WorkspaceFolders.Where(Function(f) Not String.IsNullOrWhiteSpace(f) AndAlso Directory.Exists(f)).Select(Function(f) Path.GetFullPath(f)).ToList()
        If list.Count = 0 Then list.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        Return list
    End Function

End Class

Public NotInheritable Class AppSettingsStore

    Private Sub New()
    End Sub

    Public Shared Function SettingsFile() As String
        Return Path.Combine(PathsUtil.AppDataRoot(), "settings.json")
    End Function

    Public Shared Function Load() As AppSettings
        Dim s As New AppSettings()
        Dim json = PathsUtil.ReadFileSafe(SettingsFile())
        If json IsNot Nothing Then
            Try
                Using doc = JsonDocument.Parse(json, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Dim r = doc.RootElement
                    s.ServerBaseUrl = Str(r, "serverBaseUrl", s.ServerBaseUrl)
                    s.UseCustomServer = Bool(r, "useCustomServer", False)
                    s.DeviceId = Str(r, "deviceId", Nothing)
                    s.ApiKey = Str(r, "apiKey", s.ApiKey)
                    s.StartPage = Str(r, "startPage", s.StartPage)
                    s.Port = Int(r, "port", s.Port)
                    s.LastTheme = Str(r, "lastTheme", s.LastTheme)
                    s.IncludeClaudeCodeSkills = Bool(r, "includeClaudeCodeSkills", s.IncludeClaudeCodeSkills)
                    s.IncludeClaudePlugins = Bool(r, "includeClaudePlugins", s.IncludeClaudePlugins)
                    s.SkillFolders = StrList(r, "skillFolders")
                    s.LocalToolsMode = Str(r, "localToolsMode", s.LocalToolsMode)
                    s.WorkspaceFolders = StrList(r, "workspaceFolders")
                    s.Shell = Str(r, "shell", s.Shell)
                    s.CommandTimeoutSeconds = Int(r, "commandTimeoutSeconds", s.CommandTimeoutSeconds)
                    s.IncludeClaudeUserHooks = Bool(r, "includeClaudeUserHooks", s.IncludeClaudeUserHooks)
                    s.IncludeClaudeMcp = Bool(r, "includeClaudeMcp", s.IncludeClaudeMcp)
                    s.WholeComputer = Bool(r, "wholeComputer", s.WholeComputer)
                    s.AutoUpdate = Bool(r, "autoUpdate", s.AutoUpdate)
                    s.QuickEntryHotkey = Str(r, "quickEntryHotkey", s.QuickEntryHotkey)
                    s.ScreenshotHotkey = Str(r, "screenshotHotkey", s.ScreenshotHotkey)
                    s.ScreenshotWholeScreenKey = Str(r, "screenshotWholeScreenKey", s.ScreenshotWholeScreenKey)
                    s.ScreenshotCancelKey = Str(r, "screenshotCancelKey", s.ScreenshotCancelKey)
                    s.CloseToTray = Bool(r, "closeToTray", s.CloseToTray)
                    s.ModelScreenshots = Bool(r, "modelScreenshots", s.ModelScreenshots)
                    s.ComputerUse = Bool(r, "computerUse", s.ComputerUse)
                    s.CcusageLog = Bool(r, "ccusageLog", s.CcusageLog)
                    Dim el As JsonElement
                    If r.TryGetProperty("extraHeaders", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                        For Each p In el.EnumerateObject()
                            s.ExtraHeaders(p.Name) = p.Value.ToString()
                        Next
                    End If
                End Using
            Catch
            End Try
        End If
        ' A config.json next to the exe (portable setups) sets the server and key.
        Dim sidecar = PathsUtil.ReadFileSafe(Path.Combine(AppContext.BaseDirectory, "config.json"))
        If sidecar IsNot Nothing Then
            Try
                Using doc = JsonDocument.Parse(sidecar)
                    Dim r = doc.RootElement
                    Dim sideServer = Str(r, "serverBaseUrl", Nothing)
                    ' A different address next to the exe means a different server; the main instance's address changes nothing.
                    If sideServer IsNot Nothing AndAlso Not sideServer.Trim().TrimEnd("/"c).Equals(AppSettings.DefaultServer, StringComparison.OrdinalIgnoreCase) Then
                        s.ServerBaseUrl = sideServer
                        s.UseCustomServer = True
                    End If
                    s.ApiKey = Str(r, "apiKey", s.ApiKey)
                    s.Port = Int(r, "port", s.Port)
                End Using
            Catch
            End Try
        End If
        ' The DPAPI-stored key beats the files; environment variables beat everything (tests, managed installs).
        Dim protectedKey = SecretStore.LoadKey()
        If Not String.IsNullOrWhiteSpace(protectedKey) Then s.ApiKey = protectedKey
        Dim envServer = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_SERVER")
        If Not String.IsNullOrWhiteSpace(envServer) Then
            s.ServerBaseUrl = envServer
            s.UseCustomServer = True
        End If
        If String.IsNullOrWhiteSpace(s.DeviceId) Then
            s.DeviceId = "pc-" & Guid.NewGuid().ToString("N").Substring(0, 16)
            Save(s)
        End If
        Dim envKey = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_API_KEY")
        If Not String.IsNullOrWhiteSpace(envKey) Then s.ApiKey = envKey
        If s.LocalToolsMode <> "off" AndAlso s.LocalToolsMode <> "ask" AndAlso s.LocalToolsMode <> "auto" Then s.LocalToolsMode = "ask"
        If Not LocalTools.Shells().Contains(s.Shell) Then s.Shell = LocalTools.Shells()(0)
        If s.Port < 0 OrElse s.Port > 65535 Then s.Port = AppSettings.DefaultPort
        Return s
    End Function

    Public Shared Sub Save(s As AppSettings)
        Dim payload As New Dictionary(Of String, Object) From {
            {"serverBaseUrl", s.ServerBaseUrl}, {"useCustomServer", s.UseCustomServer}, {"deviceId", s.DeviceId}, {"startPage", s.StartPage}, {"port", s.Port}, {"lastTheme", s.LastTheme},
            {"includeClaudeCodeSkills", s.IncludeClaudeCodeSkills}, {"includeClaudePlugins", s.IncludeClaudePlugins}, {"skillFolders", s.SkillFolders},
            {"localToolsMode", s.LocalToolsMode}, {"workspaceFolders", s.WorkspaceFolders}, {"shell", s.Shell},
            {"commandTimeoutSeconds", s.CommandTimeoutSeconds}, {"includeClaudeUserHooks", s.IncludeClaudeUserHooks}, {"includeClaudeMcp", s.IncludeClaudeMcp}, {"wholeComputer", s.WholeComputer}, {"extraHeaders", s.ExtraHeaders}, {"autoUpdate", s.AutoUpdate},
            {"quickEntryHotkey", s.QuickEntryHotkey}, {"screenshotHotkey", s.ScreenshotHotkey}, {"screenshotWholeScreenKey", s.ScreenshotWholeScreenKey},
            {"screenshotCancelKey", s.ScreenshotCancelKey}, {"closeToTray", s.CloseToTray}, {"modelScreenshots", s.ModelScreenshots}, {"computerUse", s.ComputerUse}, {"ccusageLog", s.CcusageLog}}
        ' The key lives in its own store (DPAPI on Windows, a private file on Linux); only in this file when that failed.
        If s.HasKey() AndAlso Not SecretStore.SaveKey(s.ApiKey) Then payload("apiKey") = s.ApiKey
        PathsUtil.WriteFileSafe(SettingsFile(), SimpleJson.StringifyPretty(payload))
        If payload.ContainsKey("apiKey") Then PlatformUtil.MakePrivate(SettingsFile())
    End Sub

    Friend Shared Function Str(r As JsonElement, name As String, fallback As String) As String
        Dim el As JsonElement
        If r.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String Then Return el.GetString()
        Return fallback
    End Function

    Friend Shared Function Int(r As JsonElement, name As String, fallback As Integer) As Integer
        Dim el As JsonElement
        Dim v As Integer
        If r.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Number AndAlso el.TryGetInt32(v) Then Return v
        Return fallback
    End Function

    Friend Shared Function Bool(r As JsonElement, name As String, fallback As Boolean) As Boolean
        Dim el As JsonElement
        If r.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.True Then Return True
        If r.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.False Then Return False
        Return fallback
    End Function

    Friend Shared Function StrList(r As JsonElement, name As String) As List(Of String)
        Dim el As JsonElement
        If r.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Array Then
            Return el.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString()).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).ToList()
        End If
        Return New List(Of String)
    End Function

End Class
