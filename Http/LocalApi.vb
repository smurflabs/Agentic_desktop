' Desktop endpoints under /api/local/* (served by the app itself, never sent to the server). Only the app's own pages
' can call them: they need the per-launch desktop token (see EmbeddedServer).
'   info, settings (+ test), open (folders in Explorer)
'   skills, skills/{id}, skills/{id}/file?path=, skills/import, skills/{id}/copy
'   mcp, mcp/{id}, mcp/{id}/reconnect, mcp/{id}/call, mcp/claude (servers found in Claude Code / Desktop), mcp/import
'   chat/prepare (tools + instructions for one chat message), chat/run (runs one tool call)
'   update (status), update/check, update/install, update/restart
'   claude (is Claude Code installed), claude/run (one message, server-sent events), claude/respond, claude/interrupt

Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports AgenticClient.Mcp

Namespace Http

    Public Class LocalApi

        Private ReadOnly _skills As SkillStore
        Private ReadOnly _mcp As McpHost
        Private ReadOnly _tools As LocalTools
        Private ReadOnly _settings As AppSettings
        Private ReadOnly _claude As New ClaudeCode()
        Public Property Updates As Updater
        ''' <summary>Closes the window and starts the updated exe (set by the window).</summary>
        Public Property RestartRequested As Action
        ''' <summary>A screenshot for the chat (set by the window): "region" (drag an area) or "screen"; Nothing when cancelled.</summary>
        Public Property CaptureScreen As Func(Of String, Task(Of Byte()))
        ''' <summary>The mouse, keyboard, windows and clipboard (computer use); Nothing where the app cannot.</summary>
        Public Property Computer As IComputerControl
        ''' <summary>Where the model's last screenshot was on the screen, and how much it was scaled down (its pixels → the screen's).</summary>
        Public Property ScreenMap As Func(Of (X As Integer, Y As Integer, Scale As Double)?)
        ''' <summary>The settings changed (the window registers its shortcuts again).</summary>
        Public Property SettingsChanged As Action
        ''' <summary>Shortcuts that could not be registered because another app uses them (set by the window).</summary>
        Public Property HotkeyProblems As Func(Of List(Of String))
        ''' <summary>Checks a global shortcut ("" when it is not one); Nothing where the app has no global shortcuts (Linux).</summary>
        Public Property NormalizeHotkey As Func(Of String, String)
        ''' <summary>Checks a key of the screenshot layer ("Enter", "Esc", "Ctrl+F2"; "" when it is not one); Nothing without that layer.</summary>
        Public Property NormalizeKey As Func(Of String, String)

        Public Sub New(skills As SkillStore, mcp As McpHost, tools As LocalTools, settings As AppSettings)
            _skills = skills
            _mcp = mcp
            _tools = tools
            _settings = settings
            ' Where the MCP servers are found: the workspace folders' .mcp.json and Claude Code's entries for them.
            McpConfig.Workspaces = Function() _settings.EffectiveWorkspaces()
            McpConfig.IncludeClaude = Function() _settings.IncludeClaudeMcp
        End Sub

        Public Async Function HandleAsync(rc As RequestContext) As Task
            Dim response = rc.ListenerContext.Response
            Dim problem As (Status As Integer, Message As String) = (0, Nothing)
            Try
                Select Case rc.Segment(2)
                    Case "info" : Await Ok(rc, Info())
                    Case "settings" : Await SettingsAsync(rc)
                    Case "open" : Await OpenAsync(rc)
                    Case "skills" : Await SkillsAsync(rc)
                    Case "mcp" : Await McpAsync(rc)
                    Case "chat" : Await ChatAsync(rc)
                    Case "update" : Await UpdateAsync(rc)
                    Case "screenshot" : Await ScreenshotAsync(rc)
                    Case "claude" : Await ClaudeAsync(rc)
                    Case "run" : Await RunProjectAsync(rc)
                    Case Else : problem = (404, "Unknown desktop endpoint.")
                End Select
            Catch ex As KeyNotFoundException
                problem = (404, ex.Message)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
                problem = (400, ex.Message)
            End Try
            If problem.Message IsNot Nothing Then Await EmbeddedServer.WriteErrorAsync(response, problem.Status, problem.Message)
        End Function

        Private Shared Function Ok(rc As RequestContext, payload As Object) As Task
            Return EmbeddedServer.WriteJsonAsync(rc.ListenerContext.Response, 200, payload)
        End Function

        Private Function Info() As Object
            Return New With {
                .version = Updater.CurrentVersion(), .machine = Environment.MachineName, .user = Environment.UserName,
                .os = Environment.OSVersion.VersionString, .runtime = Environment.Version.ToString(), .server = _settings.NormalizedBaseUrl(),
                .hasKey = _settings.HasKey(), .dataDir = PathsUtil.AppDataRoot(), .claudeHome = PathsUtil.ClaudeHome(),
                .mcpConfig = McpConfig.ConfigPath(), .localToolsMode = _settings.LocalToolsMode, .workspaces = _settings.EffectiveWorkspaces()}
        End Function

        ' ------------------------------------------------------------------ Claude Code in the Chat tab

        Private Async Function ClaudeAsync(rc As RequestContext) As Task
            Select Case rc.Segment(3)
                Case "" : Await Ok(rc, Await ClaudeCode.StatusAsync().ConfigureAwait(False))
                Case "respond" : Await Ok(rc, New With {.delivered = Await _claude.RespondAsync(rc.BodyJson()).ConfigureAwait(False)})
                Case "interrupt" : Await Ok(rc, New With {.stopped = Await _claude.InterruptAsync(JsonOf(rc.BodyJson(), "runId")).ConfigureAwait(False)})
                Case "run" : Await ClaudeRunAsync(rc)
                Case Else : Throw New KeyNotFoundException("Unknown Claude Code endpoint.")
            End Select
        End Function

        Private Shared Function JsonOf(e As JsonElement, name As String) As String
            Dim v As JsonElement
            Return If(e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String, v.GetString(), Nothing)
        End Function

        ''' <summary>One message to Claude Code, streamed back as server-sent events: "data: &lt;a line Claude printed&gt;". Closing the request stops Claude.</summary>
        Private Async Function ClaudeRunAsync(rc As RequestContext) As Task
            Dim body = rc.BodyJson()
            Dim response = rc.ListenerContext.Response
            response.StatusCode = 200
            response.ContentType = "text/event-stream; charset=utf-8"
            response.SendChunked = True
            response.Headers("Cache-Control") = "no-cache"
            Dim output = response.OutputStream
            Dim gate As New Threading.SemaphoreSlim(1, 1)
            Using cts = Threading.CancellationTokenSource.CreateLinkedTokenSource(rc.Cancellation)
                Dim send As Func(Of String, Task) =
                    Async Function(line As String)
                        Dim bytes = Encoding.UTF8.GetBytes("data: " & line.Replace(vbCr, "").Replace(vbLf, " ") & vbLf & vbLf)
                        Await gate.WaitAsync().ConfigureAwait(False)
                        Try
                            Await output.WriteAsync(bytes, cts.Token).ConfigureAwait(False)
                            Await output.FlushAsync(cts.Token).ConfigureAwait(False)
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Net.HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
                            cts.Cancel() ' the page went away (stop button, closed window): Claude is stopped
                        Finally
                            gate.Release()
                        End Try
                    End Function
                ' A comment every 15 s keeps the stream open while Claude works quietly (a long tool, a permission prompt).
                Dim ping = Task.Run(Async Function()
                                        While Not cts.IsCancellationRequested
                                            Try
                                                Await Task.Delay(15000, cts.Token).ConfigureAwait(False)
                                            Catch ex As OperationCanceledException
                                                Exit While
                                            End Try
                                            Await gate.WaitAsync().ConfigureAwait(False)
                                            Try
                                                Dim b = Encoding.UTF8.GetBytes(": ping" & vbLf & vbLf)
                                                Await output.WriteAsync(b, cts.Token).ConfigureAwait(False)
                                                Await output.FlushAsync(cts.Token).ConfigureAwait(False)
                                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Net.HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
                                                cts.Cancel()
                                            Finally
                                                gate.Release()
                                            End Try
                                        End While
                                    End Function)
                Dim failure As String = Nothing
                Try
                    Await _claude.RunAsync(body, _settings.EffectiveWorkspaces()(0), send, cts.Token).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is UnauthorizedAccessException
                    failure = ex.Message
                End Try
                If failure IsNot Nothing Then Await send(New JsonObject From {{"type", "agentic_error"}, {"message", failure}}.ToJsonString()).ConfigureAwait(False)
                cts.Cancel()
                Try
                    Await ping.ConfigureAwait(False)
                Catch ex As OperationCanceledException
                End Try
            End Using
            Try
                response.OutputStream.Close()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Net.HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
            End Try
        End Function

        ' ------------------------------------------------------------------ the Run button (run and test the project)

        ''' <summary>
        ''' run/configs (how the projects run), run/projects (the workspace folders and what each can share), run/start {id},
        ''' run/stop {id}, run/status, run/log {id, from}, run/preview {url, title} (shows a page next to the chat), run/edit (opens
        ''' .agentic/launch.json, written from what was detected when missing), run/share {id} (a project's website as a public link).
        ''' </summary>
        Private Async Function RunProjectAsync(rc As RequestContext) As Task
            If _settings.LocalToolsMode = "off" Then Throw New InvalidOperationException("Tools on this computer are switched off in the desktop settings.")
            Dim b = If(rc.Method = "POST", rc.BodyJson(), Nothing)
            Select Case rc.Segment(3)
                Case "configs"
                    Await Ok(rc, New With {.root = _tools.ProjectRoot(), .configs = _tools.RunConfigs().Select(Function(c) New With {
                        .id = c.Id, .name = c.Name, .command = c.Command, .kind = c.Kind, .url = c.Url, .port = c.Port, .source = c.Source,
                        .project = c.Project, .projectName = c.ProjectName}).ToList()})
                Case "projects"
                    Await Ok(rc, _tools.RunProjects().Select(Function(p) New With {.id = p.Id, .name = p.Name, .path = p.Path, .share = p.Share, .key = p.Key}).ToList())
                Case "share"
                    Await Ok(rc, Await _tools.ShareProjectAsync(LocalTools.Str(b, "id", ""), rc.Cancellation))
                Case "start"
                    Dim c = _tools.StartRun(LocalTools.Str(b, "id", ""))
                    Await Ok(rc, New With {.started = c.Id})
                Case "stop" : Await Ok(rc, New With {.stopped = _tools.StopRun(LocalTools.Str(b, "id", ""))})
                Case "status" : Await Ok(rc, _tools.RunStatus())
                Case "log" : Await Ok(rc, _tools.RunLog(If(LocalTools.Str(b, "id", Nothing), rc.QueryValue("id")), LocalTools.Int(b, "from", 0)))
                Case "preview"
                    Dim u As Uri = Nothing
                    If Not Uri.TryCreate(LocalTools.Str(b, "url", ""), UriKind.Absolute, u) OrElse (u.Scheme <> Uri.UriSchemeHttp AndAlso u.Scheme <> Uri.UriSchemeHttps) Then Throw New ArgumentException("Not a web address.")
                    If _tools.ShowPreview Is Nothing Then Throw New InvalidOperationException("There is no preview pane here.")
                    _tools.ShowPreview.Invoke(u.ToString(), LocalTools.Str(b, "title", u.Host))
                    Await Ok(rc, New With {.shown = True})
                Case "edit"
                    Dim file = _tools.EnsureLaunchFile()
                    PlatformUtil.OpenFile(file)
                    Await Ok(rc, New With {.path = file})
                Case Else : Throw New KeyNotFoundException("Unknown run endpoint.")
            End Select
        End Function

        ' ------------------------------------------------------------------ updates

        Private Async Function UpdateAsync(rc As RequestContext) As Task
            If Updates Is Nothing Then Throw New InvalidOperationException("Updates are not available.")
            Select Case If(rc.Method = "POST", rc.Segment(3), "")
                Case ""
                Case "check" : Await Updates.CheckAsync(install:=_settings.AutoUpdate, ct:=rc.Cancellation).ConfigureAwait(False)
                Case "install" : Await Updates.CheckAsync(install:=True, ct:=rc.Cancellation).ConfigureAwait(False)
                Case "restart"
                    If Updates.Status().State <> "ready" Then Throw New InvalidOperationException("No update is installed yet.")
                    Await Ok(rc, New With {.restarting = True}).ConfigureAwait(False)
                    RestartRequested?.Invoke()
                    Return
                Case Else : Throw New KeyNotFoundException("Unknown update action.")
            End Select
            Await Ok(rc, Updates.Status()).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ screenshots

        ''' <summary>POST ?area=region|screen: the screenshot as PNG, or {cancelled: true} when the user pressed Esc.</summary>
        Private Async Function ScreenshotAsync(rc As RequestContext) As Task
            If rc.Method <> "POST" Then Throw New KeyNotFoundException("Use POST to take a screenshot.")
            If CaptureScreen Is Nothing Then Throw New InvalidOperationException("Screenshots are not available.")
            Dim png = Await CaptureScreen(If(rc.QueryValue("area") = "screen", "screen", "region")).ConfigureAwait(False)
            If png Is Nothing Then
                Await Ok(rc, New With {.cancelled = True})
            Else
                Await EmbeddedServer.WriteBytesAsync(rc.ListenerContext.Response, 200, "image/png", png, "no-store")
            End If
        End Function

        ' ------------------------------------------------------------------ settings

        Private Function SettingsJson() As Object
            Dim k = If(_settings.ApiKey, "")
            Return New With {
                .serverBaseUrl = If(_settings.UseCustomServer, _settings.ServerBaseUrl, ""), .useCustomServer = _settings.UseCustomServer,
                .defaultServer = AppSettings.DefaultServer, .effectiveServer = _settings.NormalizedBaseUrl(), .hasKey = _settings.HasKey(),
                .apiKeyMasked = If(k.Length > 12, k.Substring(0, 8) & "…" & k.Substring(k.Length - 4), If(k.Length > 0, "•••", "")),
                .startPage = _settings.StartPage, .port = _settings.Port, .includeClaudeCodeSkills = _settings.IncludeClaudeCodeSkills,
                .includeClaudePlugins = _settings.IncludeClaudePlugins, .skillFolders = _settings.SkillFolders, .localToolsMode = _settings.LocalToolsMode,
                .workspaceFolders = _settings.WorkspaceFolders, .effectiveWorkspaces = _settings.EffectiveWorkspaces(), .shell = _settings.Shell,
                .commandTimeoutSeconds = _settings.CommandTimeoutSeconds, .includeClaudeUserHooks = _settings.IncludeClaudeUserHooks, .includeClaudeMcp = _settings.IncludeClaudeMcp, .wholeComputer = _settings.WholeComputer, .autoUpdate = _settings.AutoUpdate, .settingsFile = AppSettingsStore.SettingsFile(),
                .quickEntryHotkey = _settings.QuickEntryHotkey, .screenshotHotkey = _settings.ScreenshotHotkey, .closeToTray = _settings.CloseToTray, .modelScreenshots = _settings.ModelScreenshots, .computerUse = _settings.ComputerUse, .ccusageLog = _settings.CcusageLog,
                .screenshotWholeScreenKey = _settings.ScreenshotWholeScreenKey, .screenshotCancelKey = _settings.ScreenshotCancelKey,
                .hotkeyProblems = If(HotkeyProblems?.Invoke(), New List(Of String)),
                .platform = PlatformUtil.Name(), .shells = LocalTools.Shells(),
                .features = New With {.hotkeys = NormalizeHotkey IsNot Nothing, .tray = OperatingSystem.IsWindows(), .screenshots = CaptureScreen IsNot Nothing,
                                      .keyProtection = If(OperatingSystem.IsWindows(), "dpapi", "file")}}
        End Function

        Private Async Function SettingsAsync(rc As RequestContext) As Task
            If rc.Segment(3) = "test" Then
                Dim b = rc.BodyJson()
                Dim custom = BoolOr(b, "useCustomServer", _settings.UseCustomServer)
                Dim url = If(custom, LocalTools.Str(b, "serverBaseUrl", _settings.ServerBaseUrl), AppSettings.DefaultServer)
                Await Ok(rc, Await TestConnectionAsync(If(String.IsNullOrWhiteSpace(url), AppSettings.DefaultServer, url), LocalTools.Str(b, "apiKey", Nothing)))
                Return
            End If
            If rc.Method = "POST" Then
                Dim b = rc.BodyJson()
                Dim s = _settings
                s.UseCustomServer = BoolOr(b, "useCustomServer", s.UseCustomServer)
                Dim server = LocalTools.Str(b, "serverBaseUrl", Nothing)
                If s.UseCustomServer AndAlso Not String.IsNullOrWhiteSpace(server) Then s.ServerBaseUrl = server.Trim()
                If s.UseCustomServer AndAlso String.IsNullOrWhiteSpace(s.ServerBaseUrl) Then Throw New ArgumentException("Enter the server address, or switch off ""Use a different server"".")
                Dim key = LocalTools.Str(b, "apiKey", Nothing)
                If Not String.IsNullOrWhiteSpace(key) Then s.ApiKey = key.Trim()
                If LocalTools.Bool(b, "clearKey") Then
                    s.ApiKey = ""
                    SecretStore.ClearKey()
                End If
                s.StartPage = LocalTools.Str(b, "startPage", s.StartPage)
                s.IncludeClaudeCodeSkills = BoolOr(b, "includeClaudeCodeSkills", s.IncludeClaudeCodeSkills)
                s.IncludeClaudePlugins = BoolOr(b, "includeClaudePlugins", s.IncludeClaudePlugins)
                Dim mode = LocalTools.Str(b, "localToolsMode", s.LocalToolsMode)
                If mode = "off" OrElse mode = "ask" OrElse mode = "auto" Then s.LocalToolsMode = mode
                Dim shell = LocalTools.Str(b, "shell", s.Shell)
                If LocalTools.Shells().Contains(shell) Then s.Shell = shell
                s.AutoUpdate = BoolOr(b, "autoUpdate", s.AutoUpdate)
                s.CommandTimeoutSeconds = Math.Clamp(LocalTools.Int(b, "commandTimeoutSeconds", s.CommandTimeoutSeconds), 5, 3600)
                s.IncludeClaudeUserHooks = BoolOr(b, "includeClaudeUserHooks", s.IncludeClaudeUserHooks)
                s.IncludeClaudeMcp = BoolOr(b, "includeClaudeMcp", s.IncludeClaudeMcp)
                s.WholeComputer = BoolOr(b, "wholeComputer", s.WholeComputer)
                Dim el As JsonElement
                If b.TryGetProperty("skillFolders", el) AndAlso el.ValueKind = JsonValueKind.Array Then s.SkillFolders = AppSettingsStore.StrList(b, "skillFolders")
                If b.TryGetProperty("workspaceFolders", el) AndAlso el.ValueKind = JsonValueKind.Array Then
                    Dim folders = AppSettingsStore.StrList(b, "workspaceFolders")
                    Dim missing = folders.FirstOrDefault(Function(f) Not Directory.Exists(f))
                    If missing IsNot Nothing Then Throw New ArgumentException("Folder not found: " & missing)
                    s.WorkspaceFolders = folders
                End If
                Dim port = LocalTools.Int(b, "port", s.Port)
                If port >= 0 AndAlso port <= 65535 Then s.Port = port
                For Each name In {"quickEntryHotkey", "screenshotHotkey"}
                    Dim text = LocalTools.Str(b, name, Nothing)
                    If text Is Nothing OrElse NormalizeHotkey Is Nothing Then Continue For
                    Dim normalized = NormalizeHotkey(text)
                    If text.Trim().Length > 0 AndAlso normalized.Length = 0 Then Throw New ArgumentException($"""{text}"" is not a shortcut: use one or more of Ctrl, Alt, Shift, Win and a key, like Ctrl+Alt+Space.")
                    If name = "quickEntryHotkey" Then s.QuickEntryHotkey = normalized Else s.ScreenshotHotkey = normalized
                Next
                If s.QuickEntryHotkey.Length > 0 AndAlso String.Equals(s.QuickEntryHotkey, s.ScreenshotHotkey, StringComparison.OrdinalIgnoreCase) Then
                    Throw New ArgumentException("The two shortcuts must be different.")
                End If
                ' Keys of the screenshot layer: a key alone is fine there (Enter, Esc, Space, F2); empty = the default.
                For Each name In {"screenshotWholeScreenKey", "screenshotCancelKey"}
                    Dim text = LocalTools.Str(b, name, Nothing)
                    If text Is Nothing OrElse NormalizeKey Is Nothing Then Continue For
                    Dim normalized = If(text.Trim().Length = 0, If(name = "screenshotCancelKey", "Esc", "Enter"), NormalizeKey(text))
                    If normalized.Length = 0 Then Throw New ArgumentException($"""{text}"" is not a key: use a key like Enter, Esc, Space or F2, with Ctrl, Alt or Shift if you like.")
                    If name = "screenshotWholeScreenKey" Then s.ScreenshotWholeScreenKey = normalized Else s.ScreenshotCancelKey = normalized
                Next
                If String.Equals(s.ScreenshotWholeScreenKey, s.ScreenshotCancelKey, StringComparison.OrdinalIgnoreCase) Then
                    Throw New ArgumentException("The screenshot keys for the whole screen and for cancel must be different.")
                End If
                s.CloseToTray = BoolOr(b, "closeToTray", s.CloseToTray)
                s.ModelScreenshots = BoolOr(b, "modelScreenshots", s.ModelScreenshots)
                s.ComputerUse = BoolOr(b, "computerUse", s.ComputerUse)
                s.CcusageLog = BoolOr(b, "ccusageLog", s.CcusageLog)
                AppSettingsStore.Save(s)
                _skills.Refresh()
                SettingsChanged?.Invoke()
            End If
            Await Ok(rc, SettingsJson())
        End Function

        Private Shared Function BoolOr(b As JsonElement, name As String, fallback As Boolean) As Boolean
            Dim el As JsonElement
            If b.TryGetProperty(name, el) AndAlso (el.ValueKind = JsonValueKind.True OrElse el.ValueKind = JsonValueKind.False) Then Return el.ValueKind = JsonValueKind.True
            Return fallback
        End Function

        ''' <summary>Signs in to the server with the key (the account it belongs to, or why it failed).</summary>
        Private Async Function TestConnectionAsync(baseUrl As String, key As String) As Task(Of Object)
            key = If(String.IsNullOrWhiteSpace(key), _settings.ApiKey, key.Trim())
            Dim url = If(baseUrl, "").Trim().TrimEnd("/"c)
            If Not url.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then url = "http://" & url
            Try
                Using client As New Net.Http.HttpClient() With {.Timeout = TimeSpan.FromSeconds(10)}
                    Using req As New Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, url & "/api/auth/me")
                        If Not String.IsNullOrEmpty(key) Then req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & key)
                        Using res = Await client.SendAsync(req).ConfigureAwait(False)
                            Dim body = Await res.Content.ReadAsStringAsync().ConfigureAwait(False)
                            If res.IsSuccessStatusCode Then
                                Using doc = JsonDocument.Parse(body)
                                    Return New With {.ok = True, .user = LocalTools.Str(doc.RootElement, "displayName", LocalTools.Str(doc.RootElement, "user", "")),
                                                     .role = LocalTools.Str(doc.RootElement, "role", "")}
                                End Using
                            End If
                            Dim message = If(CInt(res.StatusCode) = 401, "The server does not accept this API key.", $"The server answered {CInt(res.StatusCode)} {res.ReasonPhrase}.")
                            Return New With {.ok = False, .message = message}
                        End Using
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is UriFormatException OrElse TypeOf ex Is JsonException
                Return New With {.ok = False, .message = "Cannot reach " & url & ": " & ex.Message}
            End Try
        End Function

        ''' <summary>Opens one of the app's folders (or a skill's) in Explorer (the file manager on Linux).</summary>
        Private Async Function OpenAsync(rc As RequestContext) As Task
            Dim b = rc.BodyJson()
            Dim target = LocalTools.Str(b, "target", "data")
            Dim folder As String = Nothing
            Select Case target
                Case "data" : folder = PathsUtil.AppDataRoot()
                Case "skills" : folder = SkillStore.DesktopRoot()
                Case "commands" : folder = SkillStore.DesktopCommandsRoot()
                Case "claude" : folder = PathsUtil.ClaudeHome()
                Case "workspace" : folder = _settings.EffectiveWorkspaces()(0)
                Case "skill"
                    Dim s = _skills.Get(LocalTools.Str(b, "id", ""))
                    If s IsNot Nothing Then folder = If(s.Kind = "skill", s.Location, Path.GetDirectoryName(s.Location))
                Case "mcp"
                    folder = Path.GetDirectoryName(McpConfig.ConfigPath())
                    If Not File.Exists(McpConfig.ConfigPath()) Then McpConfig.SaveUser(McpConfig.LoadUser())
                Case "file"
                    ' A file the chat names (a tool card's path, a changed file): shown selected in its folder. Only files in
                    ' the workspace folders, and never run: Explorer selects it.
                    Dim full = _tools.WorkspacePath(LocalTools.Str(b, "path", ""))
                    If Not File.Exists(full) Then Throw New ArgumentException("That file does not exist: " & full)
                    PlatformUtil.ShowInFolder(full)
                    Await Ok(rc, New With {.opened = full})
                    Return
            End Select
            If folder Is Nothing OrElse Not Directory.Exists(folder) Then Throw New ArgumentException("That folder does not exist.")
            PlatformUtil.OpenFolder(folder)
            Await Ok(rc, New With {.opened = folder})
        End Function

        ' ------------------------------------------------------------------ skills and commands

        Private Async Function SkillsAsync(rc As RequestContext) As Task
            Dim id = rc.Segment(3)
            Dim req = rc.ListenerContext.Request
            If id Is Nothing OrElse id = "refresh" Then
                If rc.Method = "POST" AndAlso id Is Nothing Then
                    Dim b = rc.BodyJson()
                    Dim created = _skills.Create(LocalTools.Str(b, "kind", "skill"), LocalTools.Str(b, "name", ""), LocalTools.Str(b, "description", ""), LocalTools.Str(b, "body", ""))
                    Await Ok(rc, New With {.skill = created?.ToJson()})
                    Return
                End If
                If id = "refresh" Then _skills.Refresh()
                Await Ok(rc, New With {.items = _skills.All().Select(Function(s) s.ToJson()).ToList(), .roots = _skills.Roots()})
                Return
            End If
            If id = "import" Then
                Using buffer As New MemoryStream()
                    req.InputStream.CopyTo(buffer)
                    Dim s = _skills.ImportZip(If(rc.QueryValue("name"), "skill.zip"), buffer.ToArray())
                    Await Ok(rc, New With {.skill = s?.ToJson()})
                End Using
                Return
            End If
            Dim skill = _skills.Get(id)
            If skill Is Nothing Then Throw New KeyNotFoundException("No such skill or command.")
            Select Case rc.Segment(4)
                Case "file"
                    Dim bytes = _skills.ReadFile(skill, rc.QueryValue("path"))
                    If bytes Is Nothing Then Throw New KeyNotFoundException("No such file in this skill.")
                    Await EmbeddedServer.WriteBytesAsync(rc.ListenerContext.Response, 200, EmbeddedServer.ContentType(Path.GetExtension(rc.QueryValue("path"))), bytes, "no-store")
                    Return
                Case "copy"
                    Await Ok(rc, New With {.skill = _skills.CopyToDesktop(id)?.ToJson()})
                    Return
            End Select
            Select Case rc.Method
                Case "DELETE" : Await Ok(rc, New With {.deleted = _skills.Delete(id)})
                Case "PUT"
                    Dim b = rc.BodyJson()
                    Await Ok(rc, New With {.skill = _skills.Update(id, LocalTools.Str(b, "description", skill.Description), LocalTools.Str(b, "body", skill.Body))?.ToJson()})
                Case Else
                    Await Ok(rc, New With {.skill = skill.ToJson(), .body = skill.Body, .files = skill.Files.Select(Function(f) New With {.path = f.Path, .size = f.SizeBytes}).ToList()})
            End Select
        End Function

        ' ------------------------------------------------------------------ local MCP servers

        Private Async Function McpAsync(rc As RequestContext) As Task
            Dim id = rc.Segment(3)
            If id Is Nothing Then
                If rc.Method = "POST" Then
                    Dim server = ReadServer(rc.BodyJson())
                    McpConfig.Upsert(server)
                    Await _mcp.ReconnectAsync(server.Id)
                End If
                Await Ok(rc, New With {.servers = _mcp.Snapshot(), .configPath = McpConfig.ConfigPath()})
                Return
            End If
            ' Servers to import: Claude Code, Claude Desktop, Visual Studio and VS Code (mcp/claude is the older name).
            If id = "claude" OrElse id = "found" Then
                Dim configured = McpConfig.Load().Select(Function(s) s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                Await Ok(rc, New With {.servers = FoundServers().Select(Function(x) New With {
                    .source = x.Source, .id = x.Server.Id, .name = x.Server.Name, .transport = x.Server.TransportName(), .note = x.Note,
                    .command = If(x.Server.Transport = McpTransportKind.Stdio, x.Server.Command & " " & String.Join(" ", x.Server.Args), x.Server.Endpoint),
                    .exists = configured.Contains(x.Server.Id)}).ToList()})
                Return
            End If
            If id = "import" Then
                Dim wanted = AppSettingsStore.StrList(rc.BodyJson(), "ids").ToHashSet(StringComparer.OrdinalIgnoreCase)
                Dim added As New List(Of String)
                For Each x In FoundServers()
                    If Not wanted.Contains(x.Server.Id) OrElse added.Contains(x.Server.Id) Then Continue For
                    McpConfig.Upsert(x.Server)
                    added.Add(x.Server.Id)
                Next
                Dim unused = Task.WhenAll(added.Select(Function(a) _mcp.ReconnectAsync(a)))
                Await Ok(rc, New With {.added = added})
                Return
            End If
            Select Case rc.Segment(4)
                Case "reconnect"
                    Await _mcp.ReconnectAsync(id)
                Case "approve"
                    ' A project's server: allowed (it starts) or refused (it never starts until allowed here).
                    Dim s = McpConfig.Load().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing Then Throw New KeyNotFoundException("No such MCP server.")
                    McpConfig.SetApproval(s, BoolOr(rc.BodyJson(), "allow", True))
                    Await _mcp.ReconnectAsync(id)
                Case "login", "logout"
                    ' A hosted server that uses OAuth: sign in through the browser (or forget the sign-in).
                    Dim s = McpConfig.Load().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    If s Is Nothing OrElse s.Transport = McpTransportKind.Stdio Then Throw New ArgumentException("Only HTTP servers sign in.")
                    Dim url = McpConfig.Expand(s.Endpoint, s.Root, s.PluginRoot)
                    If rc.Segment(4) = "logout" Then
                        McpOAuth.SignOut(url)
                    Else
                        Await McpOAuth.SignInAsync(url, _mcp.Get(id)?.WwwAuthenticate, s.OAuthClientId, s.OAuthClientSecret, s.OAuthScope, s.OAuthCallbackPort)
                    End If
                    Await _mcp.ReconnectAsync(id)
                Case "call"
                    Dim b = rc.BodyJson()
                    Dim argsEl As JsonElement
                    Dim argsJson = If(b.TryGetProperty("arguments", argsEl), If(argsEl.ValueKind = JsonValueKind.String, argsEl.GetString(), argsEl.GetRawText()), "{}")
                    Dim result = Await _mcp.CallToolAsync(id, LocalTools.Str(b, "tool", ""), argsJson)
                    Await Ok(rc, result.ToJson())
                    Return
                Case Nothing
                    If rc.Method = "DELETE" Then
                        McpConfig.Remove(id)
                        _mcp.Disconnect(id)
                    ElseIf rc.Method = "PATCH" Then
                        ' Enable / disable / approval switch without retyping the whole server.
                        Dim b = rc.BodyJson()
                        Dim current = McpConfig.Load().FirstOrDefault(Function(s) s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        If current Is Nothing Then Throw New KeyNotFoundException("No such MCP server.")
                        If current.Scope <> "user" Then
                            ' Claude Code's, a plugin's, the CLI's or a project's server: on/off is remembered here; its file stays as it is.
                            McpConfig.SetOff(current.Id, Not BoolOr(b, "enabled", current.Enabled))
                            Await _mcp.ReconnectAsync(id)
                            Await Ok(rc, New With {.servers = _mcp.Snapshot(), .configPath = McpConfig.ConfigPath()})
                            Return
                        End If
                        current.Enabled = BoolOr(b, "enabled", current.Enabled)
                        current.RequireApproval = BoolOr(b, "requireApproval", current.RequireApproval)
                        McpConfig.Upsert(current)
                        Await _mcp.ReconnectAsync(id)
                    End If
            End Select
            Await Ok(rc, New With {.servers = _mcp.Snapshot(), .configPath = McpConfig.ConfigPath()})
        End Function

        Private Shared Function ReadServer(b As JsonElement) As McpServerOptions
            Dim id = McpConfig.CleanId(LocalTools.Str(b, "id", LocalTools.Str(b, "name", "")))
            Dim s As New McpServerOptions With {
                .Id = id, .Name = LocalTools.Str(b, "name", id), .Command = LocalTools.Str(b, "command", Nothing), .Endpoint = LocalTools.Str(b, "endpoint", Nothing),
                .Cwd = LocalTools.Str(b, "cwd", Nothing), .Enabled = BoolOr(b, "enabled", True), .RequireApproval = BoolOr(b, "requireApproval", False),
                .TimeoutSeconds = Math.Clamp(LocalTools.Int(b, "timeoutSeconds", 60), 5, 3600)}
            Dim transport = LocalTools.Str(b, "transport", "stdio")
            s.Transport = If(transport = "http", McpTransportKind.Http, If(transport = "sse", McpTransportKind.Sse, McpTransportKind.Stdio))
            s.Args = AppSettingsStore.StrList(b, "args")
            Dim el As JsonElement
            For Each pair In {("env", s.Env), ("headers", s.Headers)}
                If b.TryGetProperty(pair.Item1, el) AndAlso el.ValueKind = JsonValueKind.Object Then
                    For Each p In el.EnumerateObject()
                        pair.Item2(p.Name) = p.Value.ToString()
                    Next
                End If
            Next
            If s.Transport = McpTransportKind.Stdio AndAlso String.IsNullOrWhiteSpace(s.Command) Then Throw New ArgumentException("Enter the command that starts the server (e.g. npx, uvx, node, python).")
            If s.Transport <> McpTransportKind.Stdio Then
                Dim u As Uri = Nothing
                If Not Uri.TryCreate(s.Endpoint, UriKind.Absolute, u) OrElse (u.Scheme <> "http" AndAlso u.Scheme <> "https") Then Throw New ArgumentException("Enter the server's http(s) URL.")
            End If
            Return s
        End Function

        ' ------------------------------------------------------------------ chat: tools that run on this computer

        ''' <summary>The model's name for an MCP tool (Claude Code style): mcp__server__tool, at most 64 characters.</summary>
        Private Function FoundServers() As List(Of (Source As String, Server As McpServerOptions, Note As String))
            Dim list = McpConfig.FindClaudeServers().Select(Function(x) (x.Source, x.Server, CStr(Nothing))).ToList()
            list.AddRange(McpConfig.FindIdeServers(_settings.EffectiveWorkspaces()))
            Return list
        End Function

        Private Shared Function McpToolName(server As String, tool As String) As String
            Dim raw = "mcp__" & server & "__" & tool
            Dim clean = New String(raw.Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c, c, "_"c)).ToArray())
            Return If(clean.Length > 64, clean.Substring(0, 64), clean)
        End Function

        Private Async Function ChatAsync(rc As RequestContext) As Task
            Dim b = rc.BodyJson()
            If rc.Segment(3) = "prepare" Then
                Await Ok(rc, Prepare(LocalTools.Str(b, "content", ""), LocalTools.Bool(b, "skills"), AppSettingsStore.StrList(b, "mcp"), LocalTools.Bool(b, "localTools"),
                                     AppSettingsStore.StrList(b, "disabledSkills").ToHashSet(StringComparer.OrdinalIgnoreCase)))
            ElseIf rc.Segment(3) = "usage" Then
                ' An answer finished: its tokens go to the Claude Code style log that npx ccusage reads.
                Dim msg As JsonElement
                Dim logged = _settings.CcusageLog AndAlso b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty("message", msg) AndAlso
                             UsageLog.Record("desktop", Updater.CurrentVersion(), LocalTools.Str(b, "chatId", ""), _settings.EffectiveWorkspaces()(0), msg, LocalTools.Int(b, "cachedTokens", 0))
                Await Ok(rc, New With {.logged = logged})
            ElseIf rc.Segment(3) = "files" Then
                ' The @ list in the message box.
                Await Ok(rc, If(_settings.LocalToolsMode = "off", New List(Of String), _tools.ListFiles(LocalTools.Str(b, "query", ""))))
            ElseIf rc.Segment(3) = "stop" Then
                ' After an answer: the Stop hooks. One that blocks (exit code 2) sends its message back as the next message.
                If _settings.LocalToolsMode = "off" Then
                    Await Ok(rc, New With {.followUp = CStr(Nothing)})
                    Return
                End If
                Dim r = Await _tools.RunHooksAsync("Stop", "", New JsonObject From {{"chat_id", LocalTools.Str(b, "chatId", "")}, {"last_message", LocalTools.Str(b, "text", "")},
                                                                                    {"stop_hook_active", LocalTools.Bool(b, "active")}}, rc.Cancellation)
                Await Ok(rc, New With {.followUp = If(r.Blocked AndAlso r.Message.Length > 0, r.Message, Nothing)})
            ElseIf rc.Segment(3) = "add-dir" Then
                Dim full = _tools.AddWorkspaceFolder(LocalTools.Str(b, "path", ""))
                AppSettingsStore.Save(_settings)
                _skills.Refresh()
                SettingsChanged?.Invoke()
                Await Ok(rc, New With {.path = full, .workspaces = _settings.EffectiveWorkspaces()})
            ElseIf rc.Segment(3) = "doctor" Then
                Await Ok(rc, Doctor())
            ElseIf rc.Segment(3) = "plugins" Then
                ' /plugin: Claude Code plugins (list, on or off, install from a marketplace, add a marketplace).
                Await Ok(rc, Await PluginsAsync(b))
            ElseIf rc.Segment(3) = "logs" Then
                ' /feedback: what the app can say about itself (its checks, version and settings, no keys).
                Dim sb As New Text.StringBuilder()
                sb.AppendLine($"Agentic desktop app {Updater.CurrentVersion()} · {Runtime.InteropServices.RuntimeInformation.OSDescription} · .NET {Environment.Version}")
                For Each r In Doctor()
                    sb.AppendLine(Text.Json.JsonSerializer.Serialize(r))
                Next
                sb.AppendLine("MCP: " & Text.Json.JsonSerializer.Serialize(_mcp.Snapshot()))
                Await Ok(rc, New With {.text = sb.ToString()})
            ElseIf rc.Segment(3) = "turn" Then
                ' The answer to this question starts: file changes from now on are its checkpoints (Rewind).
                _tools.Checkpoints?.BeginTurn(LocalTools.Str(b, "chatId", ""), LocalTools.Str(b, "messageId", ""))
                Await Ok(rc, New With {.ok = True})
            ElseIf rc.Segment(3) = "checkpoints" Then
                Await Ok(rc, If(_tools.Checkpoints Is Nothing, New List(Of Object), _tools.Checkpoints.List(LocalTools.Str(b, "chatId", ""))))
            ElseIf rc.Segment(3) = "restore" Then
                If _tools.Checkpoints Is Nothing Then Throw New InvalidOperationException("Checkpoints are not available.")
                Dim r = _tools.Checkpoints.Restore(LocalTools.Str(b, "chatId", ""), AppSettingsStore.StrList(b, "messageIds"))
                Await Ok(rc, New With {.restored = r.Restored, .failed = r.Failed})
            ElseIf rc.Segment(3) = "agentic" Then
                ' The project's AGENTIC.md: {} reads it, {content} writes it.
                Dim p = _tools.AgenticMdPath()
                Dim el As JsonElement
                If b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty("content", el) AndAlso el.ValueKind = JsonValueKind.String Then
                    IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(p))
                    IO.File.WriteAllText(p, el.GetString())
                End If
                Await Ok(rc, New With {.path = p, .exists = IO.File.Exists(p), .content = If(IO.File.Exists(p), IO.File.ReadAllText(p), "")})
            ElseIf rc.Segment(3) = "check-prompt" Then
                ' Before a message is sent: SessionStart (first time for a chat) and UserPromptSubmit hooks.
                If _settings.LocalToolsMode = "off" Then
                    Await Ok(rc, New With {.blocked = CStr(Nothing)})
                    Return
                End If
                Dim r = Await _tools.CheckPromptAsync(LocalTools.Str(b, "content", ""), LocalTools.Str(b, "chatId", Nothing), rc.Cancellation)
                Await Ok(rc, New With {.blocked = r.Blocked, .context = r.Context})
            ElseIf rc.Segment(3) = "jobs" Then
                Await Ok(rc, _tools.JobsList())
            ElseIf rc.Segment(3) = "job-stop" Then
                Dim r = _tools.StopJob(LocalTools.Str(b, "id", ""))
                Await Ok(rc, New With {.stopped = r.Stopped, .message = r.Message})
            ElseIf rc.Segment(3) = "mcp-prompts" Then
                ' The MCP servers' prompts, for the / list: /mcp__server__prompt <arguments>.
                Dim list As New List(Of Object)
                For Each c In _mcp.ConnectedServers()
                    For Each p In c.Prompts
                        list.Add(New With {.name = McpToolName(c.Options.Id, p.Name), .description = $"[{c.Options.Name}] {If(String.IsNullOrEmpty(p.Description), p.Name, p.Description)} " &
                                                                                                         String.Join(" ", p.Args.Select(Function(x) If(x.Required, "<" & x.Name & ">", "[" & x.Name & "]"))), .kind = "command"})
                    Next
                Next
                Await Ok(rc, list)
            ElseIf rc.Segment(3) = "hooks" Then
                Await Ok(rc, _tools.HooksList())
            ElseIf rc.Segment(3) = "hook-save" Then
                Await Ok(rc, New With {.file = _tools.SaveHook(LocalTools.Str(b, "scope", "project"), LocalTools.Str(b, "event", ""), LocalTools.Str(b, "matcher", ""),
                                                               LocalTools.Str(b, "command", ""), LocalTools.Int(b, "timeout", 0))})
            ElseIf rc.Segment(3) = "hook-remove" Then
                Await Ok(rc, New With {.removed = _tools.RemoveHook(LocalTools.Str(b, "file", ""), LocalTools.Str(b, "event", ""), LocalTools.Str(b, "matcher", ""), LocalTools.Str(b, "command", ""))})
            ElseIf rc.Segment(3) = "hook-test" Then
                Await Ok(rc, Await _tools.TestHookAsync(LocalTools.Str(b, "event", "PreToolUse"), LocalTools.Str(b, "command", ""), rc.Cancellation))
            ElseIf rc.Segment(3) = "agents" Then
                Await Ok(rc, _tools.AgentDefs().Select(Function(a) New With {.name = a.Name, .description = a.Description, .tools = a.Tools, .file = a.File, .scope = a.Scope}).ToList())
            ElseIf rc.Segment(3) = "agent-save" Then
                Dim file = _tools.SaveAgent(LocalTools.Str(b, "name", ""), LocalTools.Str(b, "description", ""), AppSettingsStore.StrList(b, "tools"),
                                            LocalTools.Str(b, "prompt", ""), LocalTools.Str(b, "scope", "project"))
                Await Ok(rc, New With {.file = file})
            ElseIf rc.Segment(3) = "worktree" Then
                Dim r = _tools.WorktreeCommand(LocalTools.Str(b, "arg", ""))
                If r.NewFolder IsNot Nothing Then
                    ' The worktree (or the main folder again) becomes the first workspace folder: the tools start there.
                    Dim rest = _settings.EffectiveWorkspaces().Where(Function(f) Not String.Equals(f.TrimEnd("\"c, "/"c), r.NewFolder.TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase)).ToList()
                    _settings.WorkspaceFolders = New List(Of String) From {r.NewFolder}
                    _settings.WorkspaceFolders.AddRange(rest)
                    AppSettingsStore.Save(_settings)
                    _skills.Refresh()
                    SettingsChanged?.Invoke()
                End If
                If r.List IsNot Nothing Then
                    Await Ok(rc, New With {.ok = True, .list = r.List})
                Else
                    Await Ok(rc, New With {.ok = r.Ok, .message = r.Message, .folder = r.NewFolder})
                End If
            ElseIf rc.Segment(3) = "diff" Then
                Dim d = Await _tools.DiffAsync(rc.Cancellation)
                Await Ok(rc, New With {.status = d.Status, .diff = d.Diff, .error = d.Error})
            ElseIf rc.Segment(3) = "run" Then
                Dim name = LocalTools.Str(b, "name", "")
                Dim argsJson = LocalTools.Str(b, "arguments", "{}")
                Dim args As JsonElement
                Dim valid = True
                Try
                    Using doc = JsonDocument.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
                        args = doc.RootElement.Clone()
                    End Using
                Catch ex As JsonException
                    valid = False
                End Try
                If Not valid Then
                    Await Ok(rc, New With {.ok = False, .result = "The arguments are not valid JSON."})
                    Return
                End If
                Dim result = Await RunAsync(name, argsJson, args, LocalTools.Bool(b, "approved"), rc, LocalTools.Str(b, "chatId", Nothing),
                                            LocalTools.Str(b, "mode", Nothing), LocalTools.Str(b, "id", Nothing))
                Await Ok(rc, result)
            ElseIf rc.Segment(3) = "hook-event" Then
                ' Something happened in an answer this window runs (a subagent, a compaction, a question): its hooks here.
                If _settings.LocalToolsMode = "off" Then
                    Await Ok(rc, New With {.blocked = CStr(Nothing)})
                    Return
                End If
                Dim payload As JsonElement
                If b.ValueKind <> JsonValueKind.Object OrElse Not b.TryGetProperty("payload", payload) Then payload = Nothing
                Dim r = Await _tools.PageHookAsync(LocalTools.Str(b, "event", ""), LocalTools.Str(b, "chatId", Nothing), payload, rc.Cancellation)
                Await Ok(rc, New With {.blocked = If(r.Blocked, If(String.IsNullOrWhiteSpace(r.Message), "Blocked by a hook.", r.Message), Nothing),
                                       .context = If(r.Context, If(r.Output.Length > 0, r.Output, Nothing))})
            ElseIf rc.Segment(3) = "permission-request" Then
                ' An approval for a tool on this computer: PermissionRequest hooks may answer it first.
                If _settings.LocalToolsMode = "off" Then
                    Await Ok(rc, New With {.decision = CStr(Nothing)})
                    Return
                End If
                Dim r = Await _tools.PermissionHookAsync(LocalTools.Str(b, "chatId", Nothing), LocalTools.Str(b, "id", Nothing), LocalTools.Str(b, "name", ""),
                                                         LocalTools.Str(b, "arguments", "{}"), LocalTools.Str(b, "reason", ""), rc.Cancellation)
                Await Ok(rc, New With {.decision = r.Decision, .message = r.Message})
            ElseIf rc.Segment(3) = "allow-path" Then
                ' The user allowed a path outside the workspace (a file, or its folder) while this app runs.
                Dim p = LocalTools.Str(b, "path", "")
                If p.Length = 0 Then Throw New ArgumentException("path is required.")
                _tools.AllowPath(If(LocalTools.Bool(b, "folder") AndAlso Not IO.Directory.Exists(p), IO.Path.GetDirectoryName(p), p))
                Await Ok(rc, New With {.ok = True})
            ElseIf rc.Segment(3) = "file-text" Then
                ' A file's text as it is now, so an approval card can show an overwrite as a diff.
                Await Ok(rc, New With {.text = If(_settings.LocalToolsMode = "off", Nothing, _tools.FileTextForApproval(LocalTools.Str(b, "path", "")))})
            Else
                Throw New KeyNotFoundException("Unknown chat endpoint.")
            End If
        End Function

        ''' <summary>Tools and instructions for one message: skills, the chosen local MCP servers, files and commands; and a /command the message starts with.</summary>
        Private Function Prepare(content As String, useSkills As Boolean, mcpIds As List(Of String), useLocal As Boolean, disabledSkills As HashSet(Of String)) As Object
            Dim tools As New List(Of JsonObject)
            Dim sb As New StringBuilder()
            sb.Append($"The user is chatting from the Agentic desktop app on their Windows computer ({Environment.UserName} on {Environment.MachineName}).").Append(vbLf)

            ' A message that starts with /name runs that skill or command, like Claude Code.
            Dim invoked As Object = Nothing
            Dim m = RegularExpressions.Regex.Match(If(content, ""), "^\s*/([A-Za-z0-9][\w:.-]*)(?:\s+([\s\S]*))?$")
            If m.Success Then
                Dim s = _skills.FindByName(m.Groups(1).Value)
                If s IsNot Nothing Then
                    Dim args = m.Groups(2).Value.Trim()
                    If s.Kind = "command" Then
                        sb.Append(vbLf & $"The user ran the slash command /{s.Name}. Carry out these instructions:" & vbLf & "<command>" & vbLf & SkillStore.ExpandCommand(s, args) & vbLf & "</command>" & vbLf)
                    Else
                        sb.Append(vbLf & $"The user invoked the skill /{s.Name}" & If(args.Length > 0, " with: " & args, "") & ". Follow the skill's instructions:" & vbLf &
                                  $"<skill name=""{s.Name}"">" & vbLf & s.Body & vbLf & "</skill>" & vbLf)
                        If s.Files.Count > 0 Then sb.Append("Files in the skill (read them with skill_file): " & String.Join(", ", s.Files.Take(60).Select(Function(f) f.Path)) & vbLf)
                        useSkills = True
                    End If
                    invoked = New With {.name = s.Name, .kind = s.Kind}
                ElseIf m.Groups(1).Value.StartsWith("mcp__", StringComparison.Ordinal) Then
                    ' An MCP server's prompt: its arguments from the words after it (the last one takes the rest).
                    For Each c In _mcp.ConnectedServers()
                        For Each p In c.Prompts
                            If McpToolName(c.Options.Id, p.Name) <> m.Groups(1).Value Then Continue For
                            Dim words = m.Groups(2).Value.Trim().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
                            Dim given As New Dictionary(Of String, String)
                            For i = 0 To Math.Min(words.Length, p.Args.Count) - 1
                                given(p.Args(i).Name) = If(i = p.Args.Count - 1, String.Join(" ", words.Skip(i)), words(i))
                            Next
                            Dim text = c.GetPromptAsync(p.Name, given).GetAwaiter().GetResult()
                            sb.Append(vbLf & $"The user ran the MCP prompt /{m.Groups(1).Value}. Do what it says:" & vbLf & "<prompt>" & vbLf & text & vbLf & "</prompt>" & vbLf)
                            invoked = New With {.name = m.Groups(1).Value, .kind = "command"}
                        Next
                    Next
                End If
            End If

            Dim skills = _skills.Skills().Where(Function(s) Not disabledSkills.Contains(s.Name)).ToList()
            If useSkills AndAlso skills.Count > 0 Then
                tools.Add(New JsonObject From {{"name", "Skill"}, {"x-risk", "read"}, {"description", "Load a skill's full instructions (and list its files) when the user's request matches the skill's description. Then follow them."},
                                               {"parameters", JsonNode.Parse("{""type"":""object"",""properties"":{""skill"":{""type"":""string"",""description"":""the skill name""}},""required"":[""skill""]}")}})
                tools.Add(New JsonObject From {{"name", "skill_file"}, {"x-risk", "read"}, {"description", "Read a file that belongs to a skill (scripts, references, templates)."},
                                               {"parameters", JsonNode.Parse("{""type"":""object"",""properties"":{""skill"":{""type"":""string""},""path"":{""type"":""string""}},""required"":[""skill"",""path""]}")}})
                sb.Append(vbLf & "Skills on this computer (use the Skill tool to load one when a request matches its description):" & vbLf)
                For Each s In skills.Take(80)
                    sb.Append("- ").Append(s.Name).Append(": ").Append(If(s.Description.Length > 240, s.Description.Substring(0, 240) & "…", s.Description)).Append(vbLf)
                Next
            End If

            ' The model may look at the screen (each screenshot is approved by the user; Desktop settings can switch it off).
            If _settings.ModelScreenshots AndAlso CaptureScreen IsNot Nothing Then
                tools.Add(New JsonObject From {{"name", ScreenshotTool}, {"x-risk", "ask"},
                    {"description", "Take a screenshot of the user's screen to see what they are looking at: open apps and windows, documents, errors, " &
                                    "dialogs. Use it when the user refers to something on their screen or when seeing it would help. The Agentic window steps " &
                                    "aside while it is taken, and the user approves each screenshot. The picture comes back for you to look at."},
                    {"parameters", JsonNode.Parse("{""type"":""object"",""properties"":{""area"":{""type"":""string"",""enum"":[""monitor"",""all""],""description"":""monitor: the screen the mouse is on (default); all: every monitor""}," &
                                                  """reason"":{""type"":""string"",""description"":""Why you want to see the screen (shown to the user when they approve)""}}}")}})
            End If

            ' Computer use (Desktop settings): mouse, keyboard, windows, clipboard; each action asks first in the chat.
            If _settings.ComputerUse AndAlso Computer IsNot Nothing AndAlso Computer.Problem() Is Nothing AndAlso _settings.ModelScreenshots AndAlso CaptureScreen IsNot Nothing Then
                tools.AddRange(ComputerTools.Definitions())
                sb.Append(vbLf & "You can use this computer's mouse and keyboard (computer, list_windows, window, clipboard, open_on_computer): take a screenshot first; " &
                          "x and y are pixels of the latest screenshot, and each action gives you a new one. Say what you are about to do; the user approves each action.").Append(vbLf)
            End If

            If useLocal AndAlso _settings.LocalToolsMode <> "off" Then
                tools.AddRange(_tools.Definitions())
                sb.Append(vbLf & $"You can use the user's computer: workspace folders {String.Join("; ", _settings.EffectiveWorkspaces())}; shell {_settings.Shell}. " &
                          "Read before you change files, keep edits minimal, and say what you changed.").Append(vbLf)
            End If

            Dim wanted = mcpIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            Dim mcpServers As New List(Of String)
            For Each c In _mcp.ConnectedServers()
                If Not wanted.Contains(c.Options.Id) Then Continue For
                mcpServers.Add(c.Options.Name)
                For Each t In c.Status.Tools
                    Dim name = McpToolName(c.Options.Id, t.Name)
                    If tools.Any(Function(x) x("name").ToString() = name) Then Continue For
                    Dim schema As JsonNode = Nothing
                    Try
                        schema = JsonNode.Parse(If(t.InputSchema, "{}"))
                    Catch ex As JsonException
                    End Try
                    If TypeOf schema IsNot JsonObject Then schema = JsonNode.Parse("{""type"":""object"",""properties"":{}}")
                    Dim def As New JsonObject From {{"name", name}, {"description", $"[{c.Options.Name}] " & If(t.Description, "")}, {"parameters", schema}}
                    ' "ask": the server asks before each call, in Auto mode too.
                    If c.Options.RequireApproval Then def("x-risk") = "ask"
                    tools.Add(def)
                Next
            Next
            If _mcp.ConnectedServers().Any(Function(c) wanted.Contains(c.Options.Id) AndAlso c.HasCapability("resources")) Then
                tools.Add(New JsonObject From {{"name", "list_mcp_resources"}, {"x-risk", "read"}, {"description", "List the resources (files, records, documents) the local MCP servers offer. Read one with read_mcp_resource."},
                                               {"parameters", JsonNode.Parse("{""type"":""object"",""properties"":{""server"":{""type"":""string""}}}")}})
                tools.Add(New JsonObject From {{"name", "read_mcp_resource"}, {"x-risk", "read"}, {"description", "Read a resource of a local MCP server by its URI (list_mcp_resources shows them)."},
                                               {"parameters", JsonNode.Parse("{""type"":""object"",""properties"":{""server"":{""type"":""string""},""uri"":{""type"":""string""}},""required"":[""server"",""uri""]}")}})
            End If
            If mcpServers.Count > 0 Then sb.Append(vbLf & "Local MCP servers available as tools (mcp__server__tool): " & String.Join(", ", mcpServers) & ".").Append(vbLf)

            ' The project's AGENTIC.md / AGENTS.md (edited with /agentic in the chat), and files the user named with @path.
            sb.Append(_tools.AgenticInstructions())
            If useLocal AndAlso _settings.LocalToolsMode <> "off" Then sb.Append(_tools.MentionedFiles(content))
            ' What UserPromptSubmit / SessionStart hooks printed for this message (not for the catalog the app pushes with no message).
            If Not String.IsNullOrEmpty(content) Then sb.Append(_tools.TakeHookContext())
            ' Your own subagent types (.claude/agents, .agentic/agents): the server's task tool offers them.
            Dim agents = If(useLocal AndAlso _settings.LocalToolsMode <> "off",
                            _tools.AgentDefs().Select(Function(a) CObj(New With {.name = a.Name, .description = a.Description, .tools = a.Tools, .prompt = a.Prompt})).ToList(),
                            New List(Of Object))
            ' Claude Code's permission rules from the settings files: the server applies them to this message.
            Dim rules As Object = Nothing
            If useLocal AndAlso _settings.LocalToolsMode <> "off" Then
                Dim pr = _tools.PermissionRules()
                If pr.Allow.Count + pr.Ask.Count + pr.Deny.Count > 0 Then rules = New With {.allow = pr.Allow, .ask = pr.Ask, .deny = pr.Deny}
            End If
            Return New With {.tools = tools.Take(400).ToList(), .instructions = sb.ToString(), .invoked = invoked, .device = _settings.DeviceId, .agents = agents, .rules = rules}
        End Function

        ''' <summary>/doctor in the chat: what this computer offers the model, and what is not working.</summary>
        ''' <summary>/plugin in the chat: {action: list | enable | disable | install | uninstall | marketplace-add | -remove | -update}.</summary>
        Private Async Function PluginsAsync(b As JsonElement) As Task(Of Object)
            Dim action = LocalTools.Str(b, "action", "list")
            Dim dirs = _settings.EffectiveWorkspaces()
            Select Case action
                Case "list"
                Case "enable" : Plugins.SetEnabled(LocalTools.Str(b, "id", ""), True)
                Case "disable" : Plugins.SetEnabled(LocalTools.Str(b, "id", ""), False)
                Case "install" : Await Plugins.InstallAsync(LocalTools.Str(b, "name", ""), LocalTools.Str(b, "marketplace", Nothing))
                Case "uninstall" : Plugins.Uninstall(LocalTools.Str(b, "id", ""))
                Case "marketplace-add" : Await Plugins.AddMarketplaceAsync(LocalTools.Str(b, "source", ""))
                Case "marketplace-remove" : Plugins.RemoveMarketplace(LocalTools.Str(b, "name", ""))
                Case "marketplace-update" : Await Plugins.UpdateMarketplaceAsync(LocalTools.Str(b, "name", ""))
                Case Else : Throw New ArgumentException("Unknown plugin action.")
            End Select
            If action <> "list" Then
                ' What the plugins bring is loaded again: commands and skills now, MCP servers in the background.
                _skills.Refresh()
                Dim unused = _mcp.ConnectAllAsync()
                SettingsChanged?.Invoke()
            End If
            Return New With {
                .plugins = Plugins.Installed(dirs).Select(Function(p) New With {.id = p.Id, .name = p.Name, .marketplace = p.Marketplace, .version = p.Version, .description = p.Description,
                                                                            .dir = p.Dir, .source = p.Source, .enabled = p.Enabled, .removable = p.Removable, .hooksOn = p.HooksOn,
                                                                            .counts = New With {.commands = p.Counts.Commands, .agents = p.Counts.Agents, .skills = p.Counts.Skills, .hooks = p.Counts.Hooks, .mcp = p.Counts.Mcp}}).ToList(),
                .marketplaces = Plugins.Marketplaces().Select(Function(m) New With {.name = m.Name, .source = m.Source, .dir = m.Dir, .removable = m.Removable,
                                                                                  .plugins = m.Plugins.Select(Function(x) New With {.name = x.Name, .description = x.Description, .version = x.Version}).ToList()}).ToList()}
        End Function

        Private Function Doctor() As List(Of Object)
            Dim rows As New List(Of Object)
            Dim add = Sub(ok As Boolean, what As String, detail As String) rows.Add(New With {.ok = ok, .what = what, .detail = detail})
            add(_settings.LocalToolsMode <> "off", "Tools on this computer", If(_settings.LocalToolsMode = "off", "off (desktop settings)", _settings.LocalToolsMode & " mode"))
            add(True, "Workspace folders", String.Join("; ", _settings.EffectiveWorkspaces()))
            Dim shellExe = If(OperatingSystem.IsWindows(), If(_settings.Shell = "cmd", "cmd.exe", "powershell.exe"), If(_settings.Shell = "sh", "/bin/sh", "bash"))
            add(Not OperatingSystem.IsWindows() OrElse PlatformUtil.Which(shellExe) IsNot Nothing OrElse _settings.Shell = "cmd", "Shell", shellExe)
            Dim md = _tools.AgenticMdPath()
            add(True, "AGENTIC.md", If(IO.File.Exists(md), md, "none yet (/init writes one)"))
            add(True, "Hooks", _tools.HookCount() & " (.agentic/settings.json)")
            Dim all = _skills.Skills()
            add(True, "Skills and commands", $"{all.Where(Function(s) s.Kind <> "command").Count()} skills, {all.Where(Function(s) s.Kind = "command").Count()} commands")
            Dim configured = McpConfig.Load().Where(Function(o) o.Enabled).ToList()
            Dim connected = _mcp.ConnectedServers().Select(Function(c) c.Options.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            add(configured.All(Function(o) connected.Contains(o.Id)), "Local MCP servers",
                If(configured.Count = 0, "none", String.Join(", ", configured.Select(Function(o) o.Name & ": " & If(connected.Contains(o.Id), "connected", "down")))))
            add(True, "Version", Updater.CurrentVersion())
            Return rows
        End Function

        Public Const ScreenshotTool As String = "take_screenshot"

        Private Async Function RunAsync(name As String, argsJson As String, args As JsonElement, approved As Boolean, rc As RequestContext, Optional chatId As String = Nothing,
                                        Optional mode As String = Nothing, Optional callId As String = Nothing) As Task(Of Object)
            If name = ScreenshotTool Then
                If Not _settings.ModelScreenshots OrElse CaptureScreen Is Nothing Then Return New With {.ok = False, .result = "Screenshots are switched off in the desktop app's settings."}
                Dim area = If(LocalTools.Str(args, "area", "monitor") = "all", "all", "monitor")
                Dim png = Await CaptureScreen(area).ConfigureAwait(False)
                If png Is Nothing OrElse png.Length = 0 Then Return New With {.ok = False, .result = "No screenshot was taken."}
                Dim size = ""
                Try
                    ' PNG width and height are big-endian numbers at bytes 16-23.
                    size = $" ({(CInt(png(16)) << 24) Or (CInt(png(17)) << 16) Or (CInt(png(18)) << 8) Or png(19)}×{(CInt(png(20)) << 24) Or (CInt(png(21)) << 16) Or (CInt(png(22)) << 8) Or png(23)} pixels)"
                Catch ex As IndexOutOfRangeException
                End Try
                Return New With {.ok = True, .result = $"Screenshot of {If(area = "all", "every monitor", "the screen")}{size} taken at {DateTime.Now:HH:mm:ss} on {Environment.MachineName}.",
                                 .image = New With {.mediaType = "image/png", .data = Convert.ToBase64String(png)}}
            End If
            If ComputerTools.Names.Contains(name) Then
                If Not _settings.ComputerUse Then Return New With {.ok = False, .result = "Computer use is switched off in the desktop app's settings."}
                Dim r = Await ComputerTools.RunAsync(name, args, Computer, If(ScreenMap, Function() CType(Nothing, (Integer, Integer, Double)?)),
                                                     If(CaptureScreen Is Nothing, Nothing, Function() CaptureScreen("monitor")))
                Return New With {.ok = r.Ok, .result = r.Text, .image = If(r.Png Is Nothing, Nothing, New With {.mediaType = "image/png", .data = Convert.ToBase64String(r.Png)})}
            End If
            If name = "Skill" Then
                Dim s = _skills.FindByName(LocalTools.Str(args, "skill", ""))
                If s Is Nothing Then Return New With {.ok = False, .result = "No skill with that name. Available: " & String.Join(", ", _skills.Skills().Select(Function(x) x.Name))}
                Dim files = If(s.Files.Count = 0, "", vbLf & vbLf & "Files (read with skill_file): " & String.Join(", ", s.Files.Take(100).Select(Function(f) f.Path)))
                Return New With {.ok = True, .result = $"<skill name=""{s.Name}"" folder=""{s.Location}"">" & vbLf & s.Body & vbLf & "</skill>" & files}
            End If
            If name = "skill_file" Then
                Dim s = _skills.FindByName(LocalTools.Str(args, "skill", ""))
                Dim bytes = _skills.ReadFile(s, LocalTools.Str(args, "path", ""))
                If bytes Is Nothing Then Return New With {.ok = False, .result = "No such file in that skill."}
                If bytes.Take(8000).Contains(CByte(0)) Then Return New With {.ok = True, .result = $"(binary file, {LocalTools.SizeText(bytes.Length)})"}
                Dim text = Encoding.UTF8.GetString(bytes)
                Return New With {.ok = True, .result = If(text.Length > 60000, text.Substring(0, 60000) & vbLf & "… (shortened)", text)}
            End If
            ' A local MCP server's tool: the same PreToolUse / PostToolUse hooks as the other tools (matcher mcp__server__tool).
            Dim callMcp As Func(Of JsonElement, Task(Of (Ok As Boolean, Text As String))) = Nothing
            ' Pictures the MCP tool returned (a browser screenshot): to the model, and shown with the answer.
            Dim lastImages As List(Of (MediaType As String, Data As String)) = Nothing
            If name = "list_mcp_resources" OrElse name = "read_mcp_resource" Then
                callMcp = Async Function(a As JsonElement) As Task(Of (Ok As Boolean, Text As String))
                              Dim servers = _mcp.ConnectedServers().Where(Function(c) c.HasCapability("resources")).ToList()
                              Dim server = LocalTools.Str(a, "server", "")
                              If name = "read_mcp_resource" Then
                                  Dim c = servers.FirstOrDefault(Function(x) x.Options.Id.Equals(server, StringComparison.OrdinalIgnoreCase))
                                  If c Is Nothing Then Return (False, "No connected server called that offers resources: " & String.Join(", ", servers.Select(Function(x) x.Options.Id)))
                                  Dim r = Await c.ReadResourceAsync(LocalTools.Str(a, "uri", ""))
                                  lastImages = r.Images
                                  Return (r.Ok, If(r.Error, r.Content))
                              End If
                              Dim lines As New List(Of String)
                              For Each c In servers.Where(Function(x) server.Length = 0 OrElse x.Options.Id.Equals(server, StringComparison.OrdinalIgnoreCase))
                                  For Each r In (Await c.ListAllAsync("resources/list", "resources")).Take(200)
                                      lines.Add($"{c.Options.Id}  {McpConnection.JsonStr(r, "uri", "")}  {McpConnection.JsonStr(r, "name", "")}")
                                  Next
                              Next
                              Return (True, If(lines.Count = 0, "No resources.", String.Join(vbLf, lines)))
                          End Function
            End If
            If name.StartsWith("mcp__", StringComparison.Ordinal) Then
                callMcp = Async Function(a As JsonElement) As Task(Of (Ok As Boolean, Text As String))
                              For Each c In _mcp.ConnectedServers()
                                  For Each t In c.Status.Tools
                                      If McpToolName(c.Options.Id, t.Name) <> name Then Continue For
                                      Dim r = Await c.CallToolAsync(t.Name, a.GetRawText())
                                      lastImages = r.Images
                                      Return (r.Ok, If(r.Error, If(r.Content, "(no output)")))
                                  Next
                              Next
                              Return (False, "That MCP server is not connected anymore.")
                          End Function
            End If
            Dim outcome = Await _tools.RunAsync(name, args, rc.Cancellation, chatId, mode, callId, callMcp)
            ' A picture file it read goes to the model with the result (not added to the answer: the user has the file).
            Dim image = If(outcome.Image Is Nothing, Nothing, New With {.mediaType = outcome.Image.MediaType, .data = Convert.ToBase64String(outcome.Image.Data), .name = outcome.Image.Name, .show = False})
            Dim images = If(lastImages Is Nothing OrElse lastImages.Count = 0, Nothing, lastImages.Take(4).Select(Function(i) New With {.mediaType = i.MediaType, .data = i.Data, .show = True}).ToList())
            ' A path outside the workspace: the chat page asks the user and runs the call again when they allow it.
            Dim needsAccess = If(outcome.Ok, Nothing, OutsideWorkspaceException.PathIn(outcome.Text))
            Return New With {.ok = outcome.Ok, .result = outcome.Text, .image = image, .images = images, .needsAccess = needsAccess}
        End Function

    End Class

End Namespace
