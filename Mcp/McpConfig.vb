Imports System.IO
Imports System.Text.Json
Imports System.Text.RegularExpressions

Namespace Mcp

    ''' <summary>
    ''' MCP servers live in %APPDATA%\AgenticClient\mcp.json. Both layouts are read: this app's {"servers": [...]} and the
    ''' Claude Code / Claude Desktop {"mcpServers": {"name": {command, args, env | url, headers}}}, so a config can be
    ''' pasted from either. Servers can also be imported from Claude Code (~\.claude.json) and Claude Desktop.
    ''' </summary>
    Public NotInheritable Class McpConfig

        Private Sub New()
        End Sub

        Public Shared Function ConfigPath() As String
            Return Path.Combine(PathsUtil.AppDataRoot(), "mcp.json")
        End Function

        ''' <summary>The workspace folders (their .mcp.json files and Claude Code's entries for them); set by the app.</summary>
        Public Shared Property Workspaces As Func(Of List(Of String)) = Function() New List(Of String)
        ''' <summary>Use Claude Code's and Claude Desktop's servers as they are (setting includeClaudeMcp, on by default).</summary>
        Public Shared Property IncludeClaude As Func(Of Boolean) = Function() True

        Private Shared Function Home() As String
            Return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        End Function

        ''' <summary>
        ''' Every server, from every place: Claude Code's plugins, Claude Desktop, Claude Code (~\.claude.json and its entries for
        ''' the workspaces), the Agentic CLI (~\.agentic\mcp.json), this app's mcp.json (wins per id), and each workspace's
        ''' .mcp.json (started only once allowed: a cloned repository must not run commands by itself).
        ''' </summary>
        Public Shared Function Load() As List(Of McpServerOptions)
            Dim servers As New List(Of McpServerOptions)
            Dim add = Sub(list As List(Of McpServerOptions), scope As String, source As String, root As String)
                          For Each s In list
                              s.Scope = scope
                              s.Source = source
                              If root IsNot Nothing Then s.Root = root
                              servers.RemoveAll(Function(x) x.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
                              servers.Add(s)
                          Next
                      End Sub
            Dim folders = Workspaces.Invoke()
            ' Claude Code plugins that are on (/plugin): those installed with Agentic always, Claude Code's with its other files.
            For Each p In ClaudePlugins(folders, IncludeClaude.Invoke())
                add(p.Servers, "plugin", "Claude Code plugin " & p.Plugin, Nothing)
            Next
            If IncludeClaude.Invoke() Then
                add(Parse(PathsUtil.ReadFileSafe(ClaudeDesktopFile())), "claude-desktop", "Claude Desktop", Nothing)
                Dim claudeJson = PathsUtil.ReadFileSafe(Path.Combine(Home(), ".claude.json"))
                If claudeJson IsNot Nothing Then
                    add(Parse(claudeJson), "claude", "Claude Code", Nothing)
                    Try
                        Using doc = JsonDocument.Parse(claudeJson, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                            Dim projects As JsonElement
                            If doc.RootElement.TryGetProperty("projects", projects) AndAlso projects.ValueKind = JsonValueKind.Object Then
                                For Each p In projects.EnumerateObject()
                                    Dim ws = folders.FirstOrDefault(Function(f) SamePath(f, p.Name))
                                    If ws IsNot Nothing Then add(Parse(p.Value.GetRawText()), "claude-local", "Claude Code · " & Path.GetFileName(ws.TrimEnd("\"c, "/"c)), ws)
                                Next
                            End If
                        End Using
                    Catch ex As JsonException
                    End Try
                End If
            End If
            add(Parse(PathsUtil.ReadFileSafe(Path.Combine(Home(), ".agentic", "mcp.json"))), "cli", "Agentic CLI", Nothing)
            ' A mcp.json next to the exe provides defaults; the user's file wins per id.
            For Each file In {Path.Combine(AppContext.BaseDirectory, "mcp.json"), ConfigPath()}
                add(Parse(PathsUtil.ReadFileSafe(file)), "user", "This app", Nothing)
            Next
            For Each ws In folders
                add(Parse(PathsUtil.ReadFileSafe(Path.Combine(ws, ".mcp.json"))), "project", "Project .mcp.json · " & Path.GetFileName(ws.TrimEnd("\"c, "/"c)), ws)
            Next
            ' Switched off here: servers whose files this app does not edit.
            Dim st = State()
            For Each s In servers.Where(Function(x) x.Scope <> "user" AndAlso st.Off.Contains(x.Id))
                s.Enabled = False
            Next
            Return servers
        End Function

        ' ------------------------------------------------------------------ what is remembered here: off, allowed, refused

        Private Shared ReadOnly _stateLock As New Object()

        Public Class McpState
            Public Property Off As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Public Property Approved As New HashSet(Of String)(StringComparer.Ordinal)
            Public Property Denied As New HashSet(Of String)(StringComparer.Ordinal)
        End Class

        Private Shared Function StatePath() As String
            Return Path.Combine(PathsUtil.AppDataRoot(), "mcp-state.json")
        End Function

        Public Shared Function State() As McpState
            SyncLock _stateLock
                Dim st As New McpState()
                Dim text = PathsUtil.ReadFileSafe(StatePath())
                If String.IsNullOrWhiteSpace(text) Then Return st
                Try
                    Using doc = JsonDocument.Parse(text)
                        Dim read = Sub(name As String, into As HashSet(Of String))
                                       Dim el As JsonElement
                                       If doc.RootElement.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Array Then
                                           For Each x In el.EnumerateArray()
                                               If x.ValueKind = JsonValueKind.String Then into.Add(x.GetString())
                                           Next
                                       End If
                                   End Sub
                        read("off", st.Off)
                        read("approved", st.Approved)
                        read("denied", st.Denied)
                    End Using
                Catch ex As JsonException
                End Try
                Return st
            End SyncLock
        End Function

        Private Shared Sub SaveState(st As McpState)
            SyncLock _stateLock
                PathsUtil.WriteFileSafe(StatePath(), SimpleJson.StringifyPretty(New Dictionary(Of String, Object) From {
                    {"off", st.Off.ToList()}, {"approved", st.Approved.ToList()}, {"denied", st.Denied.ToList()}}))
            End SyncLock
        End Sub

        ''' <summary>Switches off (or on) a server whose file this app does not edit (Claude Code's, a plugin's, a project's).</summary>
        Public Shared Sub SetOff(id As String, off As Boolean)
            Dim st = State()
            If off Then st.Off.Add(id) Else st.Off.Remove(id)
            SaveState(st)
        End Sub

        Private Shared Function ApprovalKey(s As McpServerOptions) As String
            Dim what = String.Join(ChrW(1), {If(s.Command, ""), String.Join(ChrW(2), s.Args), If(s.Endpoint, "")})
            Dim hash = Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(what))).Substring(0, 16)
            Return If(s.Root, "") & "|" & s.Id & "|" & hash
        End Function

        ''' <summary>Nothing: not a project server; True / False: allowed or refused; Nothing for a project server not asked yet is Approval = "ask".</summary>
        Public Shared Function Approval(s As McpServerOptions) As String
            If s.Scope <> "project" Then Return "none"
            Dim st = State()
            Dim key = ApprovalKey(s)
            If st.Denied.Contains(key) Then Return "denied"
            If st.Approved.Contains(key) Then Return "allowed"
            ' Allowed in Claude Code already (~\.claude.json enabledMcpjsonServers for that folder).
            Dim claudeJson = PathsUtil.ReadFileSafe(Path.Combine(Home(), ".claude.json"))
            If claudeJson IsNot Nothing AndAlso s.Root IsNot Nothing Then
                Try
                    Using doc = JsonDocument.Parse(claudeJson, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                        Dim projects As JsonElement
                        If doc.RootElement.TryGetProperty("projects", projects) AndAlso projects.ValueKind = JsonValueKind.Object Then
                            For Each p In projects.EnumerateObject()
                                If Not SamePath(p.Name, s.Root) Then Continue For
                                Dim list As JsonElement
                                If p.Value.TryGetProperty("enabledMcpjsonServers", list) AndAlso list.ValueKind = JsonValueKind.Array AndAlso
                                   list.EnumerateArray().Any(Function(x) x.ValueKind = JsonValueKind.String AndAlso x.GetString() = s.Key) Then Return "allowed"
                            Next
                        End If
                    End Using
                Catch ex As JsonException
                End Try
            End If
            Return "ask"
        End Function

        Public Shared Sub SetApproval(s As McpServerOptions, allow As Boolean)
            Dim st = State()
            Dim key = ApprovalKey(s)
            st.Approved.Remove(key)
            st.Denied.Remove(key)
            If allow Then st.Approved.Add(key) Else st.Denied.Add(key)
            SaveState(st)
        End Sub

        ''' <summary>Whether the server may start: switched on, and (a project's) allowed.</summary>
        Public Shared Function MayStart(s As McpServerOptions) As Boolean
            Return s.Enabled AndAlso (s.Scope <> "project" OrElse Approval(s) = "allowed")
        End Function

        ' ------------------------------------------------------------------ Claude Code's files

        Public Shared Function ClaudeDesktopFile() As String
            If OperatingSystem.IsWindows() Then Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
            If OperatingSystem.IsMacOS() Then Return Path.Combine(Home(), "Library", "Application Support", "Claude", "claude_desktop_config.json")
            Return Path.Combine(Home(), ".config", "Claude", "claude_desktop_config.json")
        End Function

        Private Shared Function SamePath(a As String, b As String) As Boolean
            Dim n = Function(p As String) Path.GetFullPath(p.Replace("/"c, Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar)
            Try
                Return String.Equals(n(a), n(b), If(OperatingSystem.IsLinux(), StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase))
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                Return False
            End Try
        End Function

        ''' <summary>
        ''' The MCP servers of the Claude Code plugins that are on (/plugin): those installed with Agentic, and Claude Code's
        ''' own (~\.claude\plugins\installed_plugins.json) when includeClaude; Agentic's on/off wins over Claude Code's.
        ''' </summary>
        Public Shared Function ClaudePlugins(folders As List(Of String), Optional includeClaude As Boolean = True) As List(Of (Plugin As String, Servers As List(Of McpServerOptions)))
            Dim found As New List(Of (String, List(Of McpServerOptions)))
            Dim lenient As New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True}
            For Each p In Global.AgenticClient.Plugins.Enabled(folders).Where(Function(x) includeClaude OrElse x.Source = "Agentic")
                Dim plugin = p.Id.Split("@"c)(0)
                Dim dir = p.Dir
                Try
                    Dim list = Parse(PathsUtil.ReadFileSafe(Path.Combine(dir, ".mcp.json")))
                    Dim manifest = PathsUtil.ReadFileSafe(Path.Combine(dir, ".claude-plugin", "plugin.json"))
                    If manifest IsNot Nothing Then
                        Using mdoc = JsonDocument.Parse(manifest, lenient)
                            Dim ms As JsonElement
                            If mdoc.RootElement.ValueKind = JsonValueKind.Object AndAlso mdoc.RootElement.TryGetProperty("mcpServers", ms) Then
                                If ms.ValueKind = JsonValueKind.String Then
                                    list.AddRange(Parse(PathsUtil.ReadFileSafe(Path.GetFullPath(Path.Combine(dir, ms.GetString())))))
                                ElseIf ms.ValueKind = JsonValueKind.Object Then
                                    list.AddRange(Parse("{""mcpServers"":" & ms.GetRawText() & "}"))
                                End If
                            End If
                        End Using
                    End If
                    For Each s In list
                        s.PluginRoot = dir
                        s.Id = CleanId("plugin_" & plugin & "_" & s.Key)
                    Next
                    If list.Count > 0 Then found.Add((plugin, list))
                Catch ex As JsonException
                End Try
            Next
            Return found
        End Function

        ''' <summary>
        ''' A value with its variables filled in: ${VAR} and ${VAR:-default} (Claude Code), ${env:VAR}, ${CLAUDE_PLUGIN_ROOT},
        ''' ${CLAUDE_PROJECT_DIR} / ${workspaceFolder}, ${userHome}, and %VAR% on Windows. Variables that are not set are added
        ''' to missing (and become empty).
        ''' </summary>
        Public Shared Function Expand(value As String, Optional projectDir As String = Nothing, Optional pluginRoot As String = Nothing,
                                      Optional extra As IDictionary(Of String, String) = Nothing, Optional missing As List(Of String) = Nothing) As String
            If String.IsNullOrEmpty(value) Then Return If(value, "")
            Dim lookup = Function(name As String) As String
                             Dim v As String = Nothing
                             If extra IsNot Nothing AndAlso extra.TryGetValue(name, v) Then Return v
                             Return Environment.GetEnvironmentVariable(name)
                         End Function
            Dim result = Regex.Replace(value, "\$\{([^}]+)\}",
                Function(m)
                    Dim body = m.Groups(1).Value
                    Dim i = body.IndexOf(":-", StringComparison.Ordinal)
                    Dim name = If(i >= 0, body.Substring(0, i), body)
                    Dim def = If(i >= 0, body.Substring(i + 2), Nothing)
                    Select Case name
                        Case "CLAUDE_PLUGIN_ROOT" : Return If(pluginRoot, "")
                        Case "CLAUDE_PROJECT_DIR", "workspaceFolder" : Return If(projectDir, If(Workspaces.Invoke().FirstOrDefault(), Environment.CurrentDirectory))
                        Case "userHome" : Return Home()
                        Case "pathSeparator", "/" : Return Path.DirectorySeparatorChar.ToString()
                    End Select
                    If name.StartsWith("env:", StringComparison.Ordinal) Then name = name.Substring(4)
                    Dim v = lookup(name)
                    If Not String.IsNullOrEmpty(v) Then Return v
                    If def IsNot Nothing Then Return def
                    missing?.Add(name)
                    Return ""
                End Function)
            If OperatingSystem.IsWindows() Then
                result = Regex.Replace(result, "%([A-Za-z_][A-Za-z0-9_]*)%", Function(m) If(lookup(m.Groups(1).Value), m.Value))
            End If
            Return result
        End Function

        ''' <summary>KEY=value lines of a .env file.</summary>
        Public Shared Function ReadEnvFile(file As String) As Dictionary(Of String, String)
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim text = PathsUtil.ReadFileSafe(file)
            If text Is Nothing Then Return d
            For Each line In text.Split(ChrW(10))
                Dim m = Regex.Match(line, "^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.]*)\s*=\s*(.*?)\s*$")
                If m.Success Then d(m.Groups(1).Value) = Regex.Replace(m.Groups(2).Value, "^(['""])(.*)\1$", "$2")
            Next
            Return d
        End Function

        ''' <summary>Servers from a JSON text in either layout (unknown or broken entries are skipped).</summary>
        Public Shared Function Parse(json As String) As List(Of McpServerOptions)
            Dim result As New List(Of McpServerOptions)
            If String.IsNullOrWhiteSpace(json) Then Return result
            Try
                Using doc = JsonDocument.Parse(json, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Dim root = doc.RootElement
                    Dim el As JsonElement
                    If root.TryGetProperty("servers", el) AndAlso el.ValueKind = JsonValueKind.Array Then
                        For Each s In el.EnumerateArray()
                            Dim o = FromElement(s, JsonStr(s, "id", Nothing))
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                    ' VS Code's mcp.json: {"servers": {"name": {…}}}.
                    If root.TryGetProperty("servers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                        For Each p In el.EnumerateObject()
                            Dim o = FromElement(p.Value, p.Name)
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                    If root.TryGetProperty("mcpServers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                        For Each p In el.EnumerateObject()
                            Dim o = FromElement(p.Value, p.Name)
                            If o IsNot Nothing Then result.Add(o)
                        Next
                    End If
                End Using
            Catch ex As JsonException
            End Try
            Return result
        End Function

        Private Shared Function FromElement(s As JsonElement, id As String) As McpServerOptions
            If s.ValueKind <> JsonValueKind.Object OrElse String.IsNullOrWhiteSpace(id) Then Return Nothing
            Dim o As New McpServerOptions With {
                .Id = CleanId(id), .Name = JsonStr(s, "name", id), .Command = JsonStr(s, "command", Nothing),
                .Endpoint = If(JsonStr(s, "endpoint", Nothing), JsonStr(s, "url", Nothing)), .Cwd = JsonStr(s, "cwd", Nothing),
                .Enabled = JsonBool(s, "enabled", Not JsonBool(s, "disabled", False)), .TimeoutSeconds = JsonInt(s, "timeoutSeconds", 60),
                .RequireApproval = JsonBool(s, "requireApproval", False), .Key = id, .EnvFile = JsonStr(s, "envFile", Nothing)}
            Dim oauth As JsonElement
            If s.TryGetProperty("oauth", oauth) AndAlso oauth.ValueKind = JsonValueKind.Object Then
                o.OAuthClientId = JsonStr(oauth, "clientId", Nothing)
                o.OAuthClientSecret = JsonStr(oauth, "clientSecret", Nothing)
                o.OAuthScope = JsonStr(oauth, "scope", Nothing)
                o.OAuthCallbackPort = JsonInt(oauth, "callbackPort", 0)
            End If
            Dim transport = If(JsonStr(s, "transport", Nothing), JsonStr(s, "type", Nothing))
            transport = If(transport, If(o.Endpoint IsNot Nothing, "http", "stdio")).ToLowerInvariant()
            o.Transport = If(transport = "sse", McpTransportKind.Sse, If(transport = "http" OrElse transport = "streamable-http" OrElse transport = "streamablehttp", McpTransportKind.Http, McpTransportKind.Stdio))
            If o.Transport = McpTransportKind.Stdio AndAlso String.IsNullOrWhiteSpace(o.Command) Then Return Nothing
            If o.Transport <> McpTransportKind.Stdio AndAlso String.IsNullOrWhiteSpace(o.Endpoint) Then Return Nothing
            Dim el As JsonElement
            If s.TryGetProperty("args", el) AndAlso el.ValueKind = JsonValueKind.Array Then o.Args = el.EnumerateArray().Select(Function(a) a.ToString()).ToList()
            If s.TryGetProperty("env", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                For Each p In el.EnumerateObject()
                    o.Env(p.Name) = p.Value.ToString()
                Next
            End If
            If s.TryGetProperty("headers", el) AndAlso el.ValueKind = JsonValueKind.Object Then
                For Each p In el.EnumerateObject()
                    o.Headers(p.Name) = p.Value.ToString()
                Next
            End If
            Return o
        End Function

        Public Shared Function CleanId(id As String) As String
            Dim clean = New String(If(id, "").Trim().Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c, Char.ToLowerInvariant(c), "-"c)).ToArray()).Trim("-"c)
            Return If(clean.Length = 0, "server", If(clean.Length > 40, clean.Substring(0, 40), clean))
        End Function

        ''' <summary>The user's servers only (not the exe's defaults).</summary>
        Public Shared Function LoadUser() As List(Of McpServerOptions)
            Return Parse(PathsUtil.ReadFileSafe(ConfigPath()))
        End Function

        Public Shared Sub SaveUser(servers As IEnumerable(Of McpServerOptions))
            PathsUtil.WriteFileSafe(ConfigPath(), SimpleJson.StringifyPretty(New Dictionary(Of String, Object) From {
                {"servers", servers.Select(Function(s) s.ToConfig()).ToList()}}))
        End Sub

        Public Shared Sub Upsert(server As McpServerOptions)
            Dim list = LoadUser()
            list.RemoveAll(Function(x) x.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
            list.Add(server)
            SaveUser(list)
        End Sub

        ''' <summary>Removes a server; one that comes from the exe's defaults is kept but switched off.</summary>
        Public Shared Function Remove(id As String) As Boolean
            Dim list = LoadUser()
            Dim removed = list.RemoveAll(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0
            Dim fromDefaults = Parse(PathsUtil.ReadFileSafe(Path.Combine(AppContext.BaseDirectory, "mcp.json"))).FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            If fromDefaults IsNot Nothing Then
                fromDefaults.Enabled = False
                list.Add(fromDefaults)
                removed = True
            End If
            SaveUser(list)
            Return removed
        End Function

        ''' <summary>Servers configured in Claude Code (user and project entries in ~\.claude.json) and Claude Desktop.</summary>
        Public Shared Function FindClaudeServers() As List(Of (Source As String, Server As McpServerOptions))
            Dim found As New List(Of (String, McpServerOptions))
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Dim claudeJson = PathsUtil.ReadFileSafe(Path.Combine(home, ".claude.json"))
            If claudeJson IsNot Nothing Then
                For Each s In Parse(claudeJson)
                    found.Add(("Claude Code", s))
                Next
                Try
                    Using doc = JsonDocument.Parse(claudeJson)
                        Dim projects As JsonElement
                        If doc.RootElement.TryGetProperty("projects", projects) AndAlso projects.ValueKind = JsonValueKind.Object Then
                            For Each p In projects.EnumerateObject()
                                For Each s In Parse(p.Value.GetRawText())
                                    found.Add(("Claude Code · " & Path.GetFileName(p.Name.TrimEnd("/"c, "\"c)), s))
                                Next
                            Next
                        End If
                    End Using
                Catch ex As JsonException
                End Try
            End If
            For Each s In Parse(PathsUtil.ReadFileSafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")))
                found.Add(("Claude Desktop", s))
            Next
            Return found
        End Function

        ''' <summary>
        ''' Servers configured for Visual Studio and VS Code (their "servers" layout):
        '''   VS Code: %APPDATA%\Code\User\mcp.json (and its profiles, and Code - Insiders), "mcp" in User\settings.json,
        '''            and .vscode\mcp.json in a workspace folder;
        '''   Visual Studio (2022 17.14+, 2026): %USERPROFILE%\.mcp.json, and .mcp.json or .vs\mcp.json in a solution folder.
        ''' Workspace and solution files are looked for in the given folders and the folders directly inside them.
        ''' VS Code variables are converted (${env:X} to %X%, ${workspaceFolder}, ${userHome}); a server that needs a value
        ''' VS Code asks for (${input:…}) comes with a note and switched off, to be completed after importing.
        ''' </summary>
        Public Shared Function FindIdeServers(folders As IEnumerable(Of String)) As List(Of (Source As String, Server As McpServerOptions, Note As String))
            Dim found As New List(Of (String, McpServerOptions, String))
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Dim appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            Dim add = Sub(source As String, file As String, folder As String)
                          For Each x In ParseIde(PathsUtil.ReadFileSafe(file), folder)
                              found.Add((source, x.Server, x.Note))
                          Next
                      End Sub
            For Each product In {("VS Code", "Code"), ("VS Code Insiders", "Code - Insiders")}
                Dim user = Path.Combine(appData, product.Item2, "User")
                If Not Directory.Exists(user) Then Continue For
                add(product.Item1, Path.Combine(user, "mcp.json"), home)
                add(product.Item1 & " settings", Path.Combine(user, "settings.json"), home)
                Dim profiles = Path.Combine(user, "profiles")
                If Directory.Exists(profiles) Then
                    For Each p In Directory.GetDirectories(profiles).Take(50)
                        add(product.Item1 & " profile", Path.Combine(p, "mcp.json"), home)
                    Next
                End If
            Next
            add("Visual Studio", Path.Combine(home, ".mcp.json"), home)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each root In If(folders, Array.Empty(Of String)())
                If String.IsNullOrWhiteSpace(root) OrElse Not Directory.Exists(root) Then Continue For
                Dim candidates As New List(Of String) From {root}
                Try
                    candidates.AddRange(Directory.GetDirectories(root).Where(Function(d) Not Path.GetFileName(d).StartsWith("."c)).Take(300))
                Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IOException
                End Try
                For Each folder In candidates
                    If Not seen.Add(folder) Then Continue For
                    Dim name = Path.GetFileName(folder.TrimEnd("\"c, "/"c))
                    add("VS Code · " & name, Path.Combine(folder, ".vscode", "mcp.json"), folder)
                    If Not String.Equals(folder, home, StringComparison.OrdinalIgnoreCase) Then add("Visual Studio · " & name, Path.Combine(folder, ".mcp.json"), folder)
                    add("Visual Studio · " & name, Path.Combine(folder, ".vs", "mcp.json"), folder)
                Next
            Next
            Return found
        End Function

        ''' <summary>The "servers" object of a Visual Studio / VS Code file (or "mcp": {"servers"} in VS Code's settings.json).</summary>
        Friend Shared Function ParseIde(json As String, folder As String) As List(Of (Server As McpServerOptions, Note As String))
            Dim result As New List(Of (McpServerOptions, String))
            If String.IsNullOrWhiteSpace(json) Then Return result
            Try
                Using doc = JsonDocument.Parse(json, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Dim root = doc.RootElement, servers As JsonElement, mcp As JsonElement
                    If root.ValueKind <> JsonValueKind.Object Then Return result
                    If Not (root.TryGetProperty("servers", servers) AndAlso servers.ValueKind = JsonValueKind.Object) Then
                        If Not (root.TryGetProperty("mcp", mcp) AndAlso mcp.ValueKind = JsonValueKind.Object AndAlso mcp.TryGetProperty("servers", servers) AndAlso servers.ValueKind = JsonValueKind.Object) Then Return result
                    End If
                    For Each p In servers.EnumerateObject()
                        Dim o = FromElement(p.Value, p.Name)
                        If o Is Nothing Then Continue For
                        Dim needsInput = False
                        Dim conv = Function(v As String) As String
                                       If v Is Nothing Then Return Nothing
                                       If v.Contains("${input:") Then needsInput = True
                                       v = Text.RegularExpressions.Regex.Replace(v, "\$\{env:([^}]+)\}", "%$1%")
                                       v = v.Replace("${workspaceFolder}", folder).Replace("${userHome}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                                       Return v.Replace("${pathSeparator}", Path.DirectorySeparatorChar.ToString())
                                   End Function
                        o.Command = conv(o.Command)
                        o.Endpoint = conv(o.Endpoint)
                        o.Cwd = conv(o.Cwd)
                        o.Args = o.Args.Select(conv).ToList()
                        For Each k In o.Env.Keys.ToList()
                            o.Env(k) = conv(o.Env(k))
                        Next
                        For Each k In o.Headers.Keys.ToList()
                            o.Headers(k) = conv(o.Headers(k))
                        Next
                        ' envFile: KEY=value lines read into the server's environment (values in "env" win).
                        Dim envFile = conv(JsonStr(p.Value, "envFile", Nothing))
                        If Not String.IsNullOrWhiteSpace(envFile) Then
                            For Each line In If(PathsUtil.ReadFileSafe(envFile), "").Split({ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                                Dim t = line.Trim()
                                Dim eq = t.IndexOf("="c)
                                If t.StartsWith("#"c) OrElse eq <= 0 Then Continue For
                                Dim key = t.Substring(0, eq).Trim()
                                If Not o.Env.ContainsKey(key) Then o.Env(key) = t.Substring(eq + 1).Trim().Trim(""""c)
                            Next
                        End If
                        Dim note As String = Nothing
                        If needsInput Then
                            o.Enabled = False
                            note = "needs a value that VS Code asks for (${input:…}): edit it after importing, then switch it on"
                        End If
                        result.Add((o, note))
                    Next
                End Using
            Catch ex As JsonException
            End Try
            Return result
        End Function

        Friend Shared Function JsonStr(e As JsonElement, name As String, fallback As String) As String
            Dim el As JsonElement
            If e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String Then Return el.GetString()
            Return fallback
        End Function

        Friend Shared Function JsonBool(e As JsonElement, name As String, fallback As Boolean) As Boolean
            Dim el As JsonElement
            If Not e.TryGetProperty(name, el) Then Return fallback
            If el.ValueKind = JsonValueKind.True Then Return True
            If el.ValueKind = JsonValueKind.False Then Return False
            Return fallback
        End Function

        Friend Shared Function JsonInt(e As JsonElement, name As String, fallback As Integer) As Integer
            Dim el As JsonElement
            Dim v As Integer
            If e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Number AndAlso el.TryGetInt32(v) Then Return v
            Return fallback
        End Function

    End Class

End Namespace
