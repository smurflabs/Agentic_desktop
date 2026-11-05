Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading

' What the Chat tab gets from this computer beyond the tools (like the CLI): the commands running in the background and the
' monitors (/bashes, /monitors), the hooks (/hooks; UserPromptSubmit and SessionStart run here too), your own subagent types
' (/agents: .claude/agents and .agentic/agents), git worktrees (/worktree) and the working tree's changes (/diff).
Partial Public Class LocalTools

    ' ------------------------------------------------------------------ /bashes, /monitors

    ''' <summary>The background commands and monitors, newest first, with the end of their output.</summary>
    Public Function JobsList() As List(Of Object)
        PruneJobs()
        Return _jobs.Values.OrderByDescending(Function(j) j.Started).Select(Function(j)
                                                                                Dim text = j.Output.ToString()
                                                                                Dim tail = If(text.Length > 1500, "…" & text.Substring(text.Length - 1500), text).TrimEnd()
                                                                                Return CObj(New With {.id = j.Id, .kind = j.Kind, .command = j.Command, .description = j.Description,
                                                                                                      .running = Not ShellEnded(j), .exitCode = If(ShellEnded(j), CType(j.ExitCode, Integer?), Nothing),
                                                                                                      .startedUtc = j.Started, .output = tail})
                                                                            End Function).ToList()
    End Function

    ''' <summary>Stops a background command or monitor (and what it started).</summary>
    Public Function StopJob(id As String) As (Stopped As Boolean, Message As String)
        Dim text = KillCommand(If(id, ""))
        Return (text.StartsWith("Stopped", StringComparison.Ordinal), text)
    End Function

    ' ------------------------------------------------------------------ /hooks, UserPromptSubmit, SessionStart

    Private Shared ReadOnly HookEvents As String() = {"PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "UserPromptSubmit", "SessionStart",
                                                      "Stop", "SubagentStart", "SubagentStop", "Notification", "PreCompact", "PostCompact"}

    ''' <summary>Every hook in the settings files, for /hooks: event, matcher, command; and the files that exist.</summary>
    Public Function HooksList() As Object
        Dim rows As New List(Of Object)
        Dim files As New List(Of String)
        For Each doc In HookDocs()
            Dim f = doc.File, root = doc.Root
            files.Add(f)
            For Each ev In HookEvents
                For Each g In If(TryCast(TryCast(root?("hooks"), JsonObject)?(ev), JsonArray), New JsonArray()).OfType(Of JsonObject)()
                    For Each h In If(TryCast(g("hooks"), JsonArray), New JsonArray()).OfType(Of JsonObject)()
                        If h("type")?.ToString() = "command" AndAlso Not String.IsNullOrWhiteSpace(h("command")?.ToString()) Then
                            rows.Add(New With {.event = ev, .matcher = If(g("matcher")?.ToString(), ""), .command = h("command").ToString(), .file = f})
                        End If
                    Next
                Next
            Next
        Next
        Return New With {.files = files, .hooks = rows}
    End Function

    ''' <summary>Chats that have had their SessionStart hooks in this run of the app.</summary>
    Private ReadOnly _sessionsStarted As New System.Collections.Concurrent.ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
    ''' <summary>What UserPromptSubmit / SessionStart hooks printed, for the next message's instructions.</summary>
    Private _hookContext As String

    ''' <summary>
    ''' Before a message is sent: SessionStart hooks the first time a chat is used here, then UserPromptSubmit hooks. One that
    ''' exits with 2 stops the message (its reason comes back); what the others print goes with the message.
    ''' </summary>
    Public Async Function CheckPromptAsync(prompt As String, chatId As String, ct As CancellationToken) As Task(Of (Blocked As String, Context As String))
        Dim printed As New List(Of String)
        Dim key = If(String.IsNullOrEmpty(chatId), "new:" & Guid.NewGuid().ToString("N"), chatId)
        If _sessionsStarted.TryAdd(key, 0) Then
            ' The matcher of a SessionStart hook is the source (startup, resume), like Claude Code's.
            Dim source = If(String.IsNullOrEmpty(chatId), "startup", "resume")
            Dim s = Await RunHooksAsync("SessionStart", source, New JsonObject From {{"session_id", If(chatId, "")}, {"source", source}}, ct).ConfigureAwait(False)
            If s.Output.Length > 0 Then printed.Add(s.Output)
            If s.Context IsNot Nothing Then printed.Add(s.Context)
        End If
        Dim u = Await RunHooksAsync("UserPromptSubmit", "", New JsonObject From {{"session_id", If(chatId, "")}, {"prompt", If(prompt, "")}}, ct).ConfigureAwait(False)
        If u.Blocked Then Return (If(String.IsNullOrWhiteSpace(u.Message), "Blocked by a hook.", u.Message), Nothing)
        If u.Output.Length > 0 Then printed.Add(u.Output)
        If u.Context IsNot Nothing Then printed.Add(u.Context)
        _hookContext = If(printed.Count = 0, Nothing, String.Join(vbLf & vbLf, printed))
        Return (Nothing, _hookContext)
    End Function

    ''' <summary>Hook events the chat page reports from an answer this app's window runs, and what each one's matcher sees.</summary>
    Private Shared ReadOnly PageEvents As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"Notification", "notification_type"}, {"PreCompact", "trigger"}, {"PostCompact", "trigger"}, {"SubagentStart", "agent_type"}, {"SubagentStop", "agent_type"}}

    ''' <summary>An event from the chat page (a subagent started, the conversation was compacted, the chat waits for the user): its hooks.</summary>
    Public Async Function PageHookAsync(eventName As String, chatId As String, payload As JsonElement, ct As CancellationToken) As Task(Of HookResult)
        Dim targetKey As String = Nothing
        If Not PageEvents.TryGetValue(If(eventName, ""), targetKey) Then Throw New ArgumentException("Unknown hook event: " & eventName)
        Dim data As New JsonObject From {{"session_id", If(chatId, "")}}
        If payload.ValueKind = JsonValueKind.Object Then
            For Each p In payload.EnumerateObject()
                If p.Name <> "hook_event_name" AndAlso p.Name <> "cwd" Then data(p.Name) = JsonNode.Parse(p.Value.GetRawText())
            Next
        End If
        Return Await RunHooksAsync(eventName, Str(payload, targetKey, ""), data, ct).ConfigureAwait(False)
    End Function

    ''' <summary>
    ''' PermissionRequest hooks answer an approval before the user does (Claude Code's decision.behavior allow | deny): Nothing
    ''' when they leave it to the user.
    ''' </summary>
    Public Async Function PermissionHookAsync(chatId As String, callId As String, name As String, argsJson As String, reason As String, ct As CancellationToken) As Task(Of (Decision As String, Message As String))
        Dim input As JsonNode = Nothing
        Try
            input = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
        Catch ex As JsonException
        End Try
        Dim r = Await RunHooksAsync("PermissionRequest", name, New JsonObject From {{"session_id", If(chatId, "")}, {"tool_use_id", If(callId, "")}, {"tool_input", input},
                                                                                  {"reason", If(reason, "")}}, ct).ConfigureAwait(False)
        If r.Blocked OrElse r.PermissionDecision = "deny" Then Return ("deny", If(String.IsNullOrWhiteSpace(r.Message), "denied by a PermissionRequest hook", r.Message))
        If r.PermissionDecision = "allow" Then Return ("allow", Nothing)
        Return (Nothing, Nothing)
    End Function

    ''' <summary>The hooks' output for the message being prepared (once), as instructions.</summary>
    Public Function TakeHookContext() As String
        Dim text = Interlocked.Exchange(_hookContext, Nothing)
        If String.IsNullOrWhiteSpace(text) Then Return ""
        Return vbLf & "Context from the user's hooks on this computer (UserPromptSubmit / SessionStart):" & vbLf & "<hook-output>" & vbLf & text.Trim() & vbLf & "</hook-output>" & vbLf
    End Function

    ' ------------------------------------------------------------------ /agents

    ''' <summary>Folders with agent files, most specific first: each workspace's .agentic and .claude, then the user's.</summary>
    Private Function AgentFolders() As List(Of (Dir As String, Scope As String))
        Dim list As New List(Of (Dir As String, Scope As String))
        For Each w In _settings.EffectiveWorkspaces()
            list.Add((Path.Combine(w, ".agentic", "agents"), "project"))
            list.Add((Path.Combine(w, ".claude", "agents"), "project"))
        Next
        Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        list.Add((Path.Combine(home, ".agentic", "agents"), "user"))
        list.Add((Path.Combine(PathsUtil.ClaudeHome(), "agents"), "user"))
        ' A Claude Code plugin's agents (/plugin) are named plugin:agent.
        Try
            For Each p In Plugins.Enabled(_settings.EffectiveWorkspaces()).Where(Function(x) _settings.IncludeClaudePlugins OrElse x.Source = "Agentic")
                For Each d In Plugins.Folders(p.Dir, "agents")
                    list.Add((d, "plugin:" & p.Name))
                Next
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Return list
    End Function

    ''' <summary>The user's subagent types (the first file of a name wins): name, description, tools, instructions, file.</summary>
    Public Function AgentDefs() As List(Of (Name As String, Description As String, Tools As List(Of String), Prompt As String, File As String, Scope As String))
        Dim list As New List(Of (Name As String, Description As String, Tools As List(Of String), Prompt As String, File As String, Scope As String))
        For Each f In AgentFolders()
            If Not Directory.Exists(f.Dir) Then Continue For
            Try
                For Each md In Directory.EnumerateFiles(f.Dir, "*.md").OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                    Dim parsed = YamlFrontMatter.Split(PathsUtil.ReadFileSafe(md))
                    Dim name = FrontText(parsed.Front, "name", Path.GetFileNameWithoutExtension(md))
                    If f.Scope.StartsWith("plugin:", StringComparison.Ordinal) Then name = f.Scope.Substring(7) & ":" & name
                    If list.Any(Function(a) a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) OrElse String.IsNullOrWhiteSpace(parsed.Body) Then Continue For
                    Dim tools = FrontText(parsed.Front, "tools", "")
                    list.Add((name, FrontText(parsed.Front, "description", ""), tools.Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0).ToList(),
                              parsed.Body.Trim(), md, f.Scope))
                    If list.Count >= 30 Then Return list
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        Next
        Return list
    End Function

    Private Shared Function FrontText(map As Dictionary(Of String, Object), key As String, fallback As String) As String
        Dim o As Object = Nothing
        If map Is Nothing OrElse Not map.TryGetValue(key, o) OrElse o Is Nothing Then Return fallback
        Dim v = If(TypeOf o Is List(Of String), String.Join(", ", DirectCast(o, List(Of String))), o.ToString()).Trim()
        Return If(v.Length = 0, fallback, v)
    End Function

    ''' <summary>A new agent file in the first workspace's .claude\agents (or the user's ~\.claude\agents); returns its path.</summary>
    Public Function SaveAgent(name As String, description As String, tools As List(Of String), prompt As String, scope As String) As String
        Dim slug = New String(If(name, "").Trim().ToLowerInvariant().Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c, c, "-"c)).ToArray()).Trim("-"c)
        If slug.Length = 0 Then Throw New ArgumentException("Give the agent a name.")
        If String.IsNullOrWhiteSpace(prompt) Then Throw New ArgumentException("Write the agent's instructions.")
        Dim dir = If(scope = "user", Path.Combine(PathsUtil.ClaudeHome(), "agents"), Path.Combine(_settings.EffectiveWorkspaces()(0), ".claude", "agents"))
        Dim file = Path.Combine(dir, slug & ".md")
        If IO.File.Exists(file) Then Throw New ArgumentException("There is an agent file with that name already: " & file)
        Directory.CreateDirectory(dir)
        Dim sb As New StringBuilder("---" & vbLf & "name: " & slug & vbLf & "description: " & If(description, "").Replace(vbCr, " ").Replace(vbLf, " ").Trim() & vbLf)
        If tools IsNot Nothing AndAlso tools.Count > 0 Then sb.Append("tools: ").Append(String.Join(", ", tools)).Append(vbLf)
        sb.Append("---" & vbLf & vbLf).Append(prompt.Trim()).Append(vbLf)
        IO.File.WriteAllText(file, sb.ToString())
        Return file
    End Function

    ' ------------------------------------------------------------------ /worktree, /diff

    ''' <summary>The folder the chat worked in before /worktree moved it (Nothing when not in a worktree).</summary>
    Public Property WorktreeHome As String

    ''' <summary>
    ''' /worktree [name] (create or reuse, then work in it), list, done (back to the main folder), remove &lt;name&gt;. Returns
    ''' what to say, and the folder to work in from now on (Nothing = unchanged).
    ''' </summary>
    Public Function WorktreeCommand(arg As String) As (Ok As Boolean, Message As String, NewFolder As String, List As String)
        Dim here = _settings.EffectiveWorkspaces()(0)
        Dim parts = If(arg, "").Trim().Split({" "c}, 2, StringSplitOptions.RemoveEmptyEntries)
        Dim verb = If(parts.Length > 0, parts(0).ToLowerInvariant(), "")
        Select Case verb
            Case "list"
                Return (True, Nothing, Nothing, If(Worktrees.List(here), "git worktree list failed (is this folder in a git repository?)"))
            Case "done", "exit", "leave"
                If WorktreeHome Is Nothing Then Return (False, "Not working in a worktree.", Nothing, Nothing)
                Dim home = WorktreeHome
                WorktreeHome = Nothing
                Return (True, $"Back in {home}. The worktree stays (/worktree remove <name> deletes it).", home, Nothing)
            Case "remove", "rm", "delete"
                If parts.Length < 2 Then Return (False, "/worktree remove <name>", Nothing, Nothing)
                Dim problem = Worktrees.Remove(If(WorktreeHome, here), parts(1), False)
                Return (problem Is Nothing, If(problem, $"Removed the worktree {Worktrees.NameFrom(parts(1))} (its branch stays)."), Nothing, Nothing)
            Case Else
                Dim made = Worktrees.Create(here, arg)
                If made.Error IsNot Nothing Then Return (False, "No worktree: " & made.Error, Nothing, Nothing)
                If WorktreeHome Is Nothing Then WorktreeHome = here
                Return (True, $"Working in {made.Folder} on branch {made.Branch}. /worktree done goes back.", made.Folder, Nothing)
        End Select
    End Function

    ''' <summary>git status --short and git diff of the first workspace (the diff cut at 300,000 characters).</summary>
    Public Async Function DiffAsync(ct As CancellationToken) As Task(Of (Status As String, Diff As String, [Error] As String))
        Dim dir = _settings.EffectiveWorkspaces()(0)
        Dim status = Await GitTextAsync(dir, "status --short", ct).ConfigureAwait(False)
        If Not status.Ok Then Return (Nothing, Nothing, If(status.Text.Contains("not a git repository"), "This folder is not in a git repository: " & dir, status.Text))
        Dim diff = Await GitTextAsync(dir, "--no-pager diff HEAD", ct).ConfigureAwait(False)
        Dim text = If(diff.Ok, diff.Text, "")
        If text.Length > 300000 Then text = text.Substring(0, 300000) & vbLf & "… (cut here)"
        Return (status.Text.TrimEnd(), text.TrimEnd(), Nothing)
    End Function

    Private Shared Async Function GitTextAsync(dir As String, args As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
        Try
            Dim psi As New ProcessStartInfo("git", args) With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True,
                                                               .WorkingDirectory = dir, .StandardOutputEncoding = New UTF8Encoding(False), .StandardErrorEncoding = New UTF8Encoding(False)}
            psi.Environment("GIT_PAGER") = "cat"
            Using p = Process.Start(psi)
                Dim outTask = p.StandardOutput.ReadToEndAsync()
                Dim errTask = p.StandardError.ReadToEndAsync()
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(30))
                    Try
                        Await p.WaitForExitAsync(cts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        Try
                            p.Kill(True)
                        Catch
                        End Try
                        Return (False, "git did not finish within 30 s")
                    End Try
                End Using
                Dim out = Await outTask.ConfigureAwait(False), err = Await errTask.ConfigureAwait(False)
                Return (p.ExitCode = 0, If(p.ExitCode = 0, out, (err & out).Trim()))
            End Using
        Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return (False, "git is not installed or could not start: " & ex.Message)
        End Try
    End Function

End Class
