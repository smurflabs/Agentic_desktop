Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading

''' <summary>
''' More of what Claude Code does, for the local tools: commands that keep running in the background, notebook cell
''' edits, hooks (PreToolUse / PostToolUse / Stop in .agentic/settings.json, Claude Code's format), files the user
''' mentions with @path, the @ file list in the message box, and /add-dir.
''' </summary>
Partial Public Class LocalTools

    ' ------------------------------------------------------------------ the tools with hooks around them

    ''' <param name="mode">The chat's permission mode (hooks get it as permission_mode).</param>
    ''' <param name="callId">The tool call's id (hooks get it as tool_use_id).</param>
    ''' <param name="other">Runs a tool that is not one of these (a local MCP server's), with the same hooks around it.</param>
    Public Async Function RunAsync(name As String, args As JsonElement, ct As CancellationToken, Optional chatId As String = Nothing, Optional mode As String = Nothing,
                                   Optional callId As String = Nothing, Optional other As Func(Of JsonElement, Task(Of (Ok As Boolean, Text As String))) = Nothing) As Task(Of (Ok As Boolean, Text As String, Image As ToolImage))
        If other Is Nothing AndAlso _settings.LocalToolsMode = "off" Then Return (False, "Tools on this computer are switched off in the desktop settings.", Nothing)
        Dim raw = If(args.ValueKind = JsonValueKind.Undefined, "{}", args.GetRawText())
        ' A deny rule in the settings files blocks the call here too (a call from another window, or an older server).
        Dim denied = MatchingRule(PermissionRules().Deny, name, args)
        If denied IsNot Nothing Then Return (False, $"Blocked by the permission rule {denied} in the settings files. Do not try it again; continue without it or ask the user.", Nothing)
        Dim common = Function() New JsonObject From {{"session_id", If(chatId, "")}, {"permission_mode", If(mode, "")}, {"tool_use_id", If(callId, "")}}
        Dim prePayload = common()
        prePayload("tool_input") = JsonNode.Parse(raw)
        Dim pre = Await RunHooksAsync("PreToolUse", name, prePayload, ct).ConfigureAwait(False)
        If pre.Blocked Then Return (False, "Blocked by a PreToolUse hook: " & pre.Message, Nothing)
        ' updatedInput: the hook rewrote the arguments; the tool runs with those.
        If pre.UpdatedInput IsNot Nothing Then
            raw = pre.UpdatedInput.ToJsonString()
            Using doc = JsonDocument.Parse(raw)
                args = doc.RootElement.Clone()
            End Using
        End If
        Dim r As (Ok As Boolean, Text As String)
        Dim image As ToolImage = Nothing
        Try
            Select Case name
                Case "fetch_url"
                    Dim fetched = Await FetchUrlAsync(args, ct).ConfigureAwait(False)
                    image = fetched.Image
                    r = (fetched.Ok, fetched.Text)
                Case "read_file"
                    ' A picture goes to the model as an image.
                    Dim target = Resolve(Str(args, "path", ""))
                    image = ReadImage(target)
                    If image IsNot Nothing Then
                        NoteRead(chatId, target)
                        r = (True, $"{target} is a picture ({SizeText(image.Data.Length)}); it follows as an image.")
                    Else
                        r = Await RunToolAsync(name, args, ct, chatId).ConfigureAwait(False)
                    End If
                Case "run_command", "run_command_background"
                    If name = "run_command_background" OrElse Bool(args, "background") Then
                        r = StartBackground(Str(args, "command", ""), CommandFolder(chatId, Str(args, "cwd", Nothing)))
                    Else
                        r = Await RunToolAsync(name, args, ct, chatId).ConfigureAwait(False)
                    End If
                Case "monitor" : r = StartMonitor(args, chatId)
                Case "preview" : r = Await PreviewToolAsync(args, ct).ConfigureAwait(False)
                Case "command_output" : r = (True, Await CommandOutputAsync(Str(args, "id", ""), Int(args, "wait_seconds", 0), ct).ConfigureAwait(False))
                Case "kill_command" : r = (True, KillCommand(Str(args, "id", "")))
                Case "edit_notebook"
                    Dim target = Resolve(Str(args, "path", ""))
                    Dim stale = StaleReason(chatId, target)
                    If stale IsNot Nothing Then
                        r = (False, stale)
                    Else
                        Checkpoints?.BeforeChange(chatId, target)
                        r = EditNotebook(target, Int(args, "cell", -1), Str(args, "mode", "replace"), Str(args, "source", ""), Str(args, "cell_type", Nothing))
                        If r.Ok Then NoteRead(chatId, target)
                    End If
                Case Else
                    r = If(other IsNot Nothing, Await other(args).ConfigureAwait(False), Await RunToolAsync(name, args, ct, chatId).ConfigureAwait(False))
            End Select
        Catch ex As OperationCanceledException When ct.IsCancellationRequested
            Throw
        Catch ex As Exception
            ' Whatever a tool throws is its result: the answer waiting for it goes on.
            r = (False, ex.Message)
        End Try
        Dim result = If(r.Text, "")
        Dim clipped = If(result.Length > 20000, result.Substring(0, 20000), result)
        Dim postPayload = common()
        postPayload("tool_input") = JsonNode.Parse(raw)
        postPayload("tool_response") = New JsonObject From {{"ok", r.Ok}, {"result", clipped}}
        If Not r.Ok Then postPayload("error") = clipped
        Dim post = Await RunHooksAsync(If(r.Ok, "PostToolUse", "PostToolUseFailure"), name, postPayload, ct).ConfigureAwait(False)
        Dim notes As New List(Of String)
        If post.Blocked Then
            notes.Add("PostToolUse hook feedback: " & post.Message)
        ElseIf post.Message.Length > 0 Then
            notes.Add("(Hook feedback: " & post.Message & ")")
        End If
        If post.Context IsNot Nothing Then notes.Add(post.Context)
        If notes.Count > 0 Then result &= vbLf & vbLf & String.Join(vbLf, notes)
        Return (r.Ok, result, image)
    End Function

    Private Function ExtraDefinitions() As List(Of JsonObject)
        Return New List(Of JsonObject) From {
            Def("run_command_background", $"Start a {ShellLabel()} command in the background on the user's computer and return its id at once; it runs until it ends or you " &
                "stop it. Use it for dev servers, watchers, long builds and tests, and anything that may wait or hang - then audit it with command_output (is it " &
                "still running, for how long, when did it last print something, its new output, warnings when it seems stuck or waits for input) and stop it with kill_command.",
                "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""cwd"":{""type"":""string""}},""required"":[""command""]}"),
            Def("command_output", "Audit a background command (from run_command_background, or a run_command still running after its wait): running or finished " &
                "(exit code), for how long, how long since it last printed something, its new output since you last looked, and a warning when it seems to " &
                "wait for input or to be stuck. wait_seconds: first wait up to that long for it to finish or print more (instead of calling again and again). " &
                "Without id: all background commands.",
                "{""type"":""object"",""properties"":{""id"":{""type"":""string""},""wait_seconds"":{""type"":""integer""}}}"),
            Def("kill_command", "Stop a background command (and what it started).",
                "{""type"":""object"",""properties"":{""id"":{""type"":""string""}},""required"":[""id""]}"),
            Def("monitor", "Watch a command's output: it runs in the background on this computer, and its output lines (only those matching filter, a " &
                "regular expression, when given) are sent into this chat as they happen - when the chat is not busy, you answer them by yourself. Use it " &
                "for logs, dev servers, watch builds and long tests instead of polling. It ends when the command exits or after timeout_seconds " &
                "(default 600, at most 3600); stop it earlier with kill_command and its id. Keep filter selective: every matching line is sent.",
                "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""description"":{""type"":""string"",""description"":""What is watched, in a few words""}," &
                """filter"":{""type"":""string"",""description"":""Regular expression: only matching lines are sent (e.g. ERROR|Listening on|FAILED)""}," &
                """timeout_seconds"":{""type"":""integer""},""cwd"":{""type"":""string""}},""required"":[""command"",""description""]}"),
            Def("edit_notebook", "Change a Jupyter notebook (.ipynb) cell: replace its source, insert a new cell before it, or delete it. Read the notebook first (cells are numbered from 0).",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string""},""cell"":{""type"":""integer"",""description"":""cell index (0-based); for insert, the new cell goes there""}," &
                """mode"":{""type"":""string"",""enum"":[""replace"",""insert"",""delete""]},""source"":{""type"":""string""},""cell_type"":{""type"":""string"",""enum"":[""code"",""markdown""]}},""required"":[""path"",""cell""]}")}
    End Function

    ' ------------------------------------------------------------------ commands (run_command, run_command_background, command_output)

    Private NotInheritable Class BackgroundJob
        Public Id As String
        Public Command As String
        ''' <summary>command (run_command, run_command_background) or monitor; /bashes and /monitors in Chat list them.</summary>
        Public Kind As String = "command"
        Public Description As String
        Public Proc As Process
        Public ReadOnly Output As New StringBuilder()
        Public ReadPos As Integer
        Public Started As DateTime
        ''' <summary>When its shell ended (something it started may still hold its output open); Nothing while it runs.</summary>
        Public Ended As DateTime?
        Public ExitCode As Integer = -1
        ''' <summary>Its output and error streams ended: nothing it started still writes to them.</summary>
        Public OutDone As Boolean
        Public ErrDone As Boolean
        Public LastOutput As DateTime?
        ''' <summary>Windows: the command's own job (everything it starts), so stopping it stops what outlived its shell too.</summary>
        Public Group As IntPtr

        Public ReadOnly Property Closed As Boolean
            Get
                Return OutDone AndAlso ErrDone
            End Get
        End Property
    End Class

    Private ReadOnly _jobs As New ConcurrentDictionary(Of String, BackgroundJob)(StringComparer.OrdinalIgnoreCase)
    Private _jobSeq As Integer

    ''' <summary>A shell that ended this long ago while its output is still open left something running that holds it (a server it started).</summary>
    Private Shared ReadOnly ExitGrace As TimeSpan = TimeSpan.FromSeconds(2)
    ''' <summary>Finished commands kept for reading, at most.</summary>
    Private Const KeepFinished As Integer = 20
    ''' <summary>A last line that asks a question and waits for an answer.</summary>
    Private Shared ReadOnly PromptLine As New Regex("(\(y/n\)|\[y/n\]|\(yes/no\)|\[yes/no\]|press any key|press enter|password\s*:?\s*$|passphrase|are you sure|" &
                                                    "do you want to (continue|proceed)|continue\?\s*$|\?\s*$)", RegexOptions.IgnoreCase)

    Private Function StartBackground(command As String, cwd As String) As (Boolean, String)
        If String.IsNullOrWhiteSpace(command) Then Return (False, "command is required.")
        Dim job = StartJob(command, cwd)
        Return (True, $"Started in the background as {job.Id}; it runs until it ends or you stop it. Check on it with command_output {{""id"":""{job.Id}""}} " &
                      "(add wait_seconds to wait for it) and stop it with kill_command.")
    End Function

    ''' <summary>Starts a command (run_command, run_command_background, monitor): its output is collected as it comes.</summary>
    ''' <param name="display">What /bashes shows as its command (the command without the lines the app adds around it).</param>
    Private Function StartJob(command As String, cwd As String, Optional display As String = Nothing) As BackgroundJob
        Dim folder = If(String.IsNullOrWhiteSpace(cwd), _settings.EffectiveWorkspaces()(0), Resolve(cwd))
        Dim job As New BackgroundJob With {.Id = "bg" & Interlocked.Increment(_jobSeq), .Command = If(display, command), .Started = DateTime.UtcNow,
                                           .Proc = New Process With {.StartInfo = ShellStartInfo(command, folder)}}
        AddHandler job.Proc.OutputDataReceived, Sub(s, e) Collect(job, False, e.Data)
        AddHandler job.Proc.ErrorDataReceived, Sub(s, e) Collect(job, True, e.Data)
        job.Proc.Start()
        ChildJob.Add(job.Proc)
        job.Group = ChildJob.ForCommand(job.Proc)
        job.Proc.BeginOutputReadLine()
        job.Proc.BeginErrorReadLine()
        _jobs(job.Id) = job
        PruneJobs()
        Return job
    End Function

    Private Shared Sub Collect(job As BackgroundJob, isError As Boolean, line As String)
        If line Is Nothing Then
            ' The end of that stream: nothing writes to it any more.
            If isError Then job.ErrDone = True Else job.OutDone = True
            Return
        End If
        SyncLock job.Output
            job.Output.AppendLine(line)
            job.LastOutput = DateTime.UtcNow
            If job.Output.Length > 2000000 Then
                Dim cut = job.Output.Length - 1000000
                job.Output.Remove(0, cut)
                job.ReadPos = Math.Max(0, job.ReadPos - cut)
            End If
        End SyncLock
    End Sub

    ''' <summary>Whether its shell has ended (noted the first time it is seen).</summary>
    Private Shared Function ShellEnded(job As BackgroundJob) As Boolean
        If job.Ended.HasValue Then Return True
        Try
            If Not job.Proc.HasExited Then Return False
        Catch ex As InvalidOperationException
        End Try
        job.ExitCode = SafeExit(job.Proc)
        job.Ended = DateTime.UtcNow
        Return True
    End Function

    ''' <summary>Done: it ended and its output too, or its shell ended a moment ago (what still holds the output is not waited for).</summary>
    Private Shared Function JobDone(job As BackgroundJob) As Boolean
        If Not ShellEnded(job) Then Return False
        Return job.Closed OrElse DateTime.UtcNow - job.Ended.Value >= ExitGrace
    End Function

    ''' <summary>
    ''' Waits up to wait for a command: "finished", "shell-ended" (its shell ended while something it started keeps running
    ''' and holds its output - a server it launched) or "running". Nothing is stopped.
    ''' </summary>
    Private Shared Async Function WaitForJobAsync(job As BackgroundJob, wait As TimeSpan, ct As CancellationToken) As Task(Of String)
        Dim deadline = DateTime.UtcNow + wait
        Do
            If ShellEnded(job) Then
                If job.Closed Then Return "finished"
                If DateTime.UtcNow - job.Ended.Value >= ExitGrace Then Return "shell-ended"
            End If
            If DateTime.UtcNow >= deadline Then Return "running"
            Await Task.Delay(100, ct).ConfigureAwait(False)
        Loop
    End Function

    ''' <summary>Its output not read yet (it counts as read now), without the folder line run_command adds at its end.</summary>
    Private Shared Function TakeOutput(job As BackgroundJob) As String
        Return StripCwd(TakeRawOutput(job))
    End Function

    Private Shared Function TakeRawOutput(job As BackgroundJob) As String
        SyncLock job.Output
            Dim fresh = job.Output.ToString(job.ReadPos, job.Output.Length - job.ReadPos)
            job.ReadPos = job.Output.Length
            Return fresh
        End SyncLock
    End Function

    Private Shared Function OutputLength(job As BackgroundJob) As Integer
        SyncLock job.Output
            Return job.Output.Length
        End SyncLock
    End Function

    ''' <summary>Drops a finished command whose output was returned (what it may have left running is not stopped).</summary>
    Private Sub ForgetJob(job As BackgroundJob)
        Dim gone As BackgroundJob = Nothing
        If Not _jobs.TryRemove(job.Id, gone) Then Return
        ChildJob.Release(job.Group)
        job.Group = IntPtr.Zero
        job.Proc.Dispose()
    End Sub

    ''' <summary>Only the newest finished commands are kept.</summary>
    Private Sub PruneJobs()
        Dim finished = _jobs.Values.Where(Function(j) j.Closed AndAlso j.Ended.HasValue).OrderBy(Function(j) j.Started).ToList()
        For Each j In finished.Take(Math.Max(0, finished.Count - KeepFinished))
            ForgetJob(j)
        Next
    End Sub

    ''' <summary>
    ''' command_output: how a command is doing - running for how long, or its exit code; how long since it last printed
    ''' something; its new output; and a warning when it seems to wait for input or to be stuck. waitSeconds: first wait up
    ''' to that long for it to finish or print more. Without an id: all of them.
    ''' </summary>
    Private Async Function CommandOutputAsync(id As String, waitSeconds As Integer, ct As CancellationToken) As Task(Of String)
        If String.IsNullOrWhiteSpace(id) Then Return If(_jobs.IsEmpty, "There are no background commands.", "Background commands:" & vbLf & ListJobs(vbLf))
        Dim job As BackgroundJob = Nothing
        If Not _jobs.TryGetValue(id, job) Then Return $"No background command {id}. " & If(_jobs.IsEmpty, "There are none.", $"There are: {ListJobs("; ")}.")
        ' Waits only while there is nothing new to report.
        If waitSeconds > 0 AndAlso Not JobDone(job) AndAlso OutputLength(job) = job.ReadPos Then
            Dim from = OutputLength(job)
            Dim deadline = DateTime.UtcNow.AddSeconds(Math.Min(3600, waitSeconds))
            While Not JobDone(job) AndAlso OutputLength(job) = from AndAlso DateTime.UtcNow < deadline
                Await Task.Delay(200, ct).ConfigureAwait(False)
            End While
            ' Output comes in pieces: a moment more for the rest of it.
            If Not JobDone(job) AndAlso OutputLength(job) > from Then Await Task.Delay(500, ct).ConfigureAwait(False)
        End If
        Return JobReport(job)
    End Function

    Private Shared Function JobReport(job As BackgroundJob) As String
        Dim now = DateTime.UtcNow
        Dim fresh = TakeOutput(job)
        Dim state = If(Not ShellEnded(job), $"running for {Span(now - job.Started)}",
                       $"finished with exit code {job.ExitCode} after {Span(job.Ended.Value - job.Started)}" &
                       If(job.Closed, "", "; something it started is still running and printing"))
        Dim quiet = If(job.LastOutput.HasValue, $"last output {Span(now - job.LastOutput.Value)} ago", "no output yet")
        Dim sb As New StringBuilder($"{job.Id} ({ShortCommand(job.Command)}): {state}; {quiet}.")
        Dim warning = JobWarning(job, now)
        If warning.Length > 0 Then sb.Append(vbLf).Append(warning)
        If fresh.Length > 30000 Then fresh = "…" & fresh.Substring(fresh.Length - 30000)
        sb.Append(vbLf).Append(If(fresh.Length = 0, "(no new output)", fresh.TrimEnd()))
        Return sb.ToString()
    End Function

    ''' <summary>Something looks wrong with a command that is still running: a question waiting for an answer, or a long silence.</summary>
    Private Shared Function JobWarning(job As BackgroundJob, now As DateTime) As String
        If ShellEnded(job) Then Return ""
        Dim idle = now - If(job.LastOutput, job.Started)
        Dim last As String
        SyncLock job.Output
            last = job.Output.ToString().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).Select(Function(l) l.Trim()).LastOrDefault(Function(l) l.Length > 0)
        End SyncLock
        If idle.TotalSeconds >= 5 AndAlso Not String.IsNullOrEmpty(last) AndAlso PromptLine.IsMatch(last) Then
            Return $"Warning: its last line looks like a question waiting for an answer (""{If(last.Length > 160, last.Substring(last.Length - 160), last)}""). " &
                   "Commands get no keyboard input here: stop it with kill_command and run it so it does not ask (for example -y, --yes, -Force, -Confirm:$false, --no-input, -NonInteractive)."
        End If
        If idle.TotalMinutes >= 2 Then
            Return $"Warning: it has printed nothing for {Span(idle)}. It may be waiting for input, stuck, or busy without printing: if it should have " &
                   "printed something by now, stop it with kill_command and find out why."
        End If
        Return ""
    End Function

    ''' <summary>45 s, 3 min 20 s, 1 h 5 min.</summary>
    Friend Shared Function Span(t As TimeSpan) As String
        Dim s = CLng(Math.Max(0, Math.Round(t.TotalSeconds)))
        If s < 60 Then Return $"{s} s"
        If s < 3600 Then Return $"{s \ 60} min" & If(s Mod 60 > 0, $" {s Mod 60} s", "")
        Dim m = (s \ 60) Mod 60
        Return $"{s \ 3600} h" & If(m > 0, $" {m} min", "")
    End Function

    Private Shared Function ShortCommand(command As String) As String
        Dim c = If(command, "").Trim().Split({vbCr, vbLf}, StringSplitOptions.None)(0)
        Return If(c.Length > 80, c.Substring(0, 80) & "…", c)
    End Function

    ''' <summary>
    ''' monitor: a background command whose new output lines (those matching the filter) go to the chat every 2 seconds, and
    ''' how it ended; the server answers them when the chat is not busy.
    ''' </summary>
    Private Function StartMonitor(args As JsonElement, chatId As String) As (Boolean, String)
        If String.IsNullOrWhiteSpace(chatId) Then Return (False, "monitor works in a chat: its output goes into the conversation.")
        Dim description = Str(args, "description", "command")
        If description.Length > 60 Then description = description.Substring(0, 60)
        Dim filter As Regex = Nothing
        Dim pattern = Str(args, "filter", Nothing)
        If Not String.IsNullOrWhiteSpace(pattern) Then
            Try
                filter = New Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))
            Catch ex As ArgumentException
                Return (False, "filter is not a valid regular expression: " & ex.Message)
            End Try
        End If
        Dim seconds = Math.Clamp(Int(args, "timeout_seconds", 600), 10, 3600)
        Dim command = Str(args, "command", "")
        If String.IsNullOrWhiteSpace(command) Then Return (False, "command is required.")
        Dim job = StartJob(command, Str(args, "cwd", Nothing))
        job.Kind = "monitor"
        job.Description = description
        Dim unused = Task.Run(Function() WatchMonitorAsync(job, chatId, description, filter, TimeSpan.FromSeconds(seconds)))
        Return (True, $"Monitor {job.Id} started: watching ""{description}""{If(filter Is Nothing, "", $" for lines matching /{pattern}/")} for up to {seconds \ 60} min. " &
                      $"Its output lines arrive in this chat as they happen; stop it with kill_command {{""id"":""{job.Id}""}}.")
    End Function

    Private Async Function WatchMonitorAsync(job As BackgroundJob, chatId As String, description As String, filter As Regex, timeout As TimeSpan) As Task
        Dim pos = 0
        Dim rest = ""
        Dim deadline = DateTime.UtcNow + timeout
        Dim lines As New List(Of String)
        Dim lastSent = DateTime.UtcNow
        Dim status As String = Nothing
        While status Is Nothing
            Await Task.Delay(1000).ConfigureAwait(False)
            Dim fresh As String
            SyncLock job.Output
                pos = Math.Min(pos, job.Output.Length)
                fresh = job.Output.ToString(pos, job.Output.Length - pos)
                pos = job.Output.Length
            End SyncLock
            Dim parts = (rest & fresh).Replace(vbCrLf, vbLf).Split(ChrW(10))
            rest = parts(parts.Length - 1)
            For i = 0 To parts.Length - 2
                Dim l = parts(i).TrimEnd()
                If l.Length > 0 AndAlso (filter Is Nothing OrElse filter.IsMatch(l)) AndAlso lines.Count < 300 Then lines.Add(l)
            Next
            Dim gone = Not _jobs.ContainsKey(job.Id)
            Dim exited = False
            Try
                exited = job.Proc.HasExited
            Catch ex As InvalidOperationException
                exited = True
            End Try
            If gone Then
                status = "stopped"
            ElseIf exited Then
                status = $"the command exited with code {SafeExit(job.Proc)}"
            ElseIf DateTime.UtcNow > deadline Then
                KillCommand(job.Id)
                status = "stopped: its time limit ran out"
            End If
            If status IsNot Nothing AndAlso rest.Trim().Length > 0 AndAlso (filter Is Nothing OrElse filter.IsMatch(rest)) Then lines.Add(rest.Trim())
            If lines.Count > 0 AndAlso ((DateTime.UtcNow - lastSent).TotalSeconds >= 2 OrElse status IsNot Nothing) OrElse status IsNot Nothing Then
                Await SendMonitorEventsAsync(chatId, job.Id, description, lines, status).ConfigureAwait(False)
                lines.Clear()
                lastSent = DateTime.UtcNow
            End If
        End While
    End Function

    Private Shared Function SafeExit(p As Process) As Integer
        Try
            Return p.ExitCode
        Catch ex As InvalidOperationException
            Return -1
        End Try
    End Function

    ''' <summary>The new lines (and how it ended) to the chat on the Agentic server; a failed send only loses those lines.</summary>
    Private Async Function SendMonitorEventsAsync(chatId As String, monitorId As String, description As String, lines As List(Of String), status As String) As Task
        Try
            Dim body = New JsonObject From {{"monitor", monitorId}, {"description", description},
                                            {"lines", New JsonArray(lines.Select(Function(l) CType(JsonValue.Create(l), JsonNode)).ToArray())}}
            If status IsNot Nothing Then body("status") = status
            Using req As New HttpRequestMessage(HttpMethod.Post, _settings.NormalizedBaseUrl() & "/api/chat/conversations/" & Uri.EscapeDataString(chatId) & "/monitor")
                If _settings.HasKey() Then req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & _settings.ApiKey)
                req.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                Using res = Await Http.SendAsync(req).ConfigureAwait(False)
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is InvalidOperationException
        End Try
    End Function

    Private Function KillCommand(id As String) As String
        Dim job As BackgroundJob = Nothing
        If Not _jobs.TryRemove(id, job) Then Return $"No background command {id}." & If(_jobs.IsEmpty, "", $" There are: {ListJobs("; ")}.")
        If job.Group <> IntPtr.Zero Then
            ' Its own job: everything it started, even what outlived its shell.
            ChildJob.Terminate(job.Group)
            job.Group = IntPtr.Zero
        Else
            Try
                If Not job.Proc.HasExited Then job.Proc.Kill(True)
            Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            End Try
        End If
        job.Proc.Dispose()
        Return $"Stopped {job.Id} ({ShortCommand(job.Command)})."
    End Function

    Private Function ListJobs(separator As String) As String
        Dim now = DateTime.UtcNow
        Return String.Join(separator, _jobs.Values.OrderBy(Function(j) j.Started).Select(Function(j) $"{j.Id}: {ShortCommand(j.Command)} (" &
            If(Not ShellEnded(j), $"running for {Span(now - j.Started)}", If(j.Closed, $"exit {j.ExitCode}", $"exit {j.ExitCode}, something it started still runs")) & ")"))
    End Function

    ''' <summary>Stops every background command (the app is closing).</summary>
    Public Sub StopBackgroundCommands()
        For Each id In _jobs.Keys.ToList()
            KillCommand(id)
        Next
    End Sub

    ' ------------------------------------------------------------------ notebooks

    Private Shared Function EditNotebook(path As String, cell As Integer, mode As String, source As String, cellType As String) As (Boolean, String)
        If Not path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) Then Return (False, "edit_notebook works on .ipynb files.")
        If Not File.Exists(path) Then Return (False, "No such notebook: " & path)
        Dim root = JsonNode.Parse(File.ReadAllText(path))
        Dim cells = TryCast(root?("cells"), JsonArray)
        If cells Is Nothing Then Return (False, "This is not a Jupyter notebook (it has no cells).")
        Dim count = cells.Count
        If cell < 0 OrElse cell > count OrElse (mode <> "insert" AndAlso cell >= count) Then Return (False, $"No cell {cell}: the notebook has {count} cells (0-{count - 1}).")
        Select Case mode
            Case "delete"
                cells.RemoveAt(cell)
            Case "insert"
                cells.Insert(cell, NewCell(If(cellType, "code"), source))
            Case Else
                Dim c = TryCast(cells(cell), JsonObject)
                If c Is Nothing Then Return (False, $"Cell {cell} is not a notebook cell.")
                Dim kind = If(cellType, c("cell_type")?.ToString())
                cells(cell) = NewCell(If(kind, "code"), source, TryCast(c("metadata")?.DeepClone(), JsonObject), c("id")?.ToString())
        End Select
        Dim opts As New JsonSerializerOptions With {.WriteIndented = True, .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping}
        File.WriteAllText(path, root.ToJsonString(opts) & vbLf)
        Dim verb = If(mode = "delete", "Deleted", If(mode = "insert", "Inserted", "Replaced"))
        Return (True, $"{verb} cell {cell} in {path} (now {cells.Count} cells).")
    End Function

    Private Shared Function NewCell(kind As String, source As String, Optional metadata As JsonObject = Nothing, Optional id As String = Nothing) As JsonObject
        Dim lines As New JsonArray()
        Dim parts = If(source, "").Replace(vbCrLf, vbLf).Split(ChrW(10))
        For i = 0 To parts.Length - 1
            If i < parts.Length - 1 Then
                lines.Add(parts(i) & vbLf)
            ElseIf parts(i).Length > 0 Then
                lines.Add(parts(i))
            End If
        Next
        Dim c As New JsonObject From {{"cell_type", If(kind = "markdown", "markdown", "code")}}
        If id IsNot Nothing Then c("id") = id
        c("metadata") = If(metadata, New JsonObject())
        c("source") = lines
        If kind <> "markdown" Then
            c("execution_count") = Nothing
            c("outputs") = New JsonArray()
        End If
        Return c
    End Function

    ' ------------------------------------------------------------------ hooks

    ''' <summary>Claude Code tool names for the matcher ("Edit|Write" matches edit_file and write_file).</summary>
    Private Shared ReadOnly ClaudeNames As New Dictionary(Of String, String) From {
        {"write_file", "Write"}, {"edit_file", "Edit"}, {"run_command", "Bash"}, {"run_command_background", "Bash"}, {"read_file", "Read"}, {"grep", "Grep"}, {"glob_files", "Glob"},
        {"list_dir", "LS"}, {"edit_notebook", "NotebookEdit"}, {"monitor", "Monitor"}}

    ''' <summary>
    ''' Settings files with hooks: ~/.agentic and each workspace's .agentic or .claude; ~/.claude (Claude Code's own) only when
    ''' Desktop settings say so.
    ''' </summary>
    Private Function HookFiles() As List(Of String)
        Dim list As New List(Of String) From {IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentic", "settings.json")}
        If _settings.IncludeClaudeUserHooks Then list.Add(IO.Path.Combine(PathsUtil.ClaudeHome(), "settings.json"))
        For Each r In _settings.EffectiveWorkspaces()
            list.Add(IO.Path.Combine(r, ".agentic", "settings.json"))
            list.Add(IO.Path.Combine(r, ".agentic", "settings.local.json"))
            list.Add(IO.Path.Combine(r, ".claude", "settings.json"))
            list.Add(IO.Path.Combine(r, ".claude", "settings.local.json"))
        Next
        Return list
    End Function

    ''' <summary>
    ''' The commands of an event's hooks whose matcher fits target (a tool name, a session's source, a compaction's trigger…; ""
    ''' for events without one, where matchers do not count), each with its time limit ("timeout" in seconds, default 60).
    ''' </summary>
    ''' <summary>
    ''' The settings files that exist, read, and the hooks of each Claude Code plugin whose hooks run (/plugin: installed with
    ''' Agentic, or a Claude Code plugin turned on here), with ${CLAUDE_PLUGIN_ROOT} filled in.
    ''' </summary>
    Friend Function HookDocs() As List(Of (File As String, Root As JsonNode))
        Dim docs As New List(Of (File As String, Root As JsonNode))
        For Each f In HookFiles()
            If Not File.Exists(f) Then Continue For
            Try
                docs.Add((f, JsonNode.Parse(File.ReadAllText(f), documentOptions:=New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})))
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        Next
        Try
            For Each p In Plugins.Enabled(_settings.EffectiveWorkspaces()).Where(Function(x) x.HooksOn)
                Dim h = Plugins.HooksOf(p.Dir)
                If h.Count > 0 Then docs.Add(("plugin " & p.Name & " (" & p.Dir & ")", New JsonObject From {{"hooks", h}}))
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Return docs
    End Function

    Private Function HookCommands(eventName As String, tool As String) As List(Of (Command As String, Timeout As Integer))
        Dim cmds As New List(Of (Command As String, Timeout As Integer))
        For Each doc In HookDocs()
            Dim root = doc.Root
            Dim groups = TryCast(TryCast(root?("hooks"), JsonObject)?(eventName), JsonArray)
            If groups Is Nothing Then Continue For
            For Each g In groups.OfType(Of JsonObject)()
                Dim matcher = If(g("matcher")?.ToString(), "")
                Dim fits = matcher.Length = 0 OrElse matcher = "*" OrElse tool = "*" OrElse String.IsNullOrEmpty(tool)
                If Not fits Then
                    Try
                        Dim re As New Regex($"^(?:{matcher})$", RegexOptions.IgnoreCase)
                        Dim claudeName As String = Nothing
                        fits = re.IsMatch(tool) OrElse (ClaudeNames.TryGetValue(tool, claudeName) AndAlso re.IsMatch(claudeName))
                    Catch ex As ArgumentException
                        fits = False
                    End Try
                End If
                If Not fits Then Continue For
                For Each h In If(TryCast(g("hooks"), JsonArray), New JsonArray()).OfType(Of JsonObject)()
                    If h("type")?.ToString() <> "command" OrElse String.IsNullOrWhiteSpace(h("command")?.ToString()) Then Continue For
                    Dim seconds = 60
                    Dim t = TryCast(h("timeout"), JsonValue)
                    Dim n As Double
                    If t IsNot Nothing AndAlso t.TryGetValue(n) AndAlso n >= 1 Then seconds = CInt(Math.Min(n, 3600))
                    cmds.Add((h("command").ToString(), seconds))
                Next
            Next
        Next
        Return cmds
    End Function

    Public Function HookCount() As Integer
        Return {"PreToolUse", "PostToolUse", "Stop"}.Sum(Function(e) HookCommands(e, "*").Count)
    End Function

    ''' <summary>Events whose hooks get the tool's name (their target is a tool).</summary>
    Private Shared ReadOnly ToolEvents As New HashSet(Of String)(StringComparer.Ordinal) From {"PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest"}

    ''' <summary>
    ''' Runs the event's hooks (the payload as JSON on stdin), like Claude Code: exit code 2 blocks (stderr is the reason); exit 0
    ''' with a JSON answer can block, allow or deny, rewrite the tool's input or add context; plain output is kept for the model.
    ''' </summary>
    Public Async Function RunHooksAsync(eventName As String, tool As String, payload As JsonObject, ct As CancellationToken) As Task(Of HookResult)
        Dim result As New HookResult()
        Dim cmds = HookCommands(eventName, tool)
        If cmds.Count = 0 Then Return result
        ' What hooks that succeeded printed: UserPromptSubmit and SessionStart hand it to the model with the message.
        Dim printed As New List(Of String)
        Dim claudeName As String = Nothing
        payload("hook_event_name") = eventName
        If ToolEvents.Contains(eventName) Then payload("tool_name") = If(ClaudeNames.TryGetValue(tool, claudeName), claudeName, tool)
        payload("cwd") = _settings.EffectiveWorkspaces()(0)
        Dim input = payload.ToJsonString()
        Dim notes As New List(Of String)
        For Each hook In cmds
            Dim cmd = hook.Command
            Dim psi = ShellStartInfo(cmd, _settings.EffectiveWorkspaces()(0))
            psi.RedirectStandardInput = True
            psi.Environment("CLAUDE_PROJECT_DIR") = _settings.EffectiveWorkspaces()(0)
            psi.Environment("AGENTIC_PROJECT_DIR") = _settings.EffectiveWorkspaces()(0)
            Using proc As New Process With {.StartInfo = psi}
                Try
                    proc.Start()
                Catch ex As ComponentModel.Win32Exception
                    notes.Add($"Hook ""{cmd}"" did not start: {ex.Message}")
                    Continue For
                End Try
                Dim outTask = proc.StandardOutput.ReadToEndAsync()
                Dim errTask = proc.StandardError.ReadToEndAsync()
                Try
                    Await proc.StandardInput.WriteAsync(input).ConfigureAwait(False)
                    proc.StandardInput.Close()
                Catch ex As IOException
                End Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(hook.Timeout))
                    Try
                        Await proc.WaitForExitAsync(cts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        Try
                            proc.Kill(True)
                        Catch
                        End Try
                        notes.Add($"Hook ""{cmd}"" took longer than {hook.Timeout} s and was stopped.")
                        Continue For
                    End Try
                End Using
                Dim stdout = (Await outTask.ConfigureAwait(False)).Trim()
                Dim stderr = (Await errTask.ConfigureAwait(False)).Trim()
                If proc.ExitCode = 2 Then
                    result.Blocked = True
                    result.Message = If(stderr.Length > 0, stderr, If(stdout.Length > 0, stdout, "Blocked by the hook: " & cmd))
                    Exit For
                End If
                If proc.ExitCode <> 0 AndAlso (stderr.Length > 0 OrElse stdout.Length > 0) Then
                    Dim said = If(stderr.Length > 0, stderr, stdout)
                    notes.Add($"Hook ""{cmd}"" exited with {proc.ExitCode}: {If(said.Length > 2000, said.Substring(0, 2000), said)}")
                ElseIf proc.ExitCode = 0 Then
                    ReadHookAnswer(stdout, result, printed)
                    If result.Blocked Then Exit For
                End If
            End Using
        Next
        If Not result.Blocked Then result.Message = String.Join(vbLf, notes)
        result.Output = String.Join(vbLf & vbLf, printed)
        Return result
    End Function

    ' ------------------------------------------------------------------ @ mentions, the @ list, /add-dir

    ''' <summary>Files and folders named with @path in the message: their text goes with it (at most 6, 60,000 characters each).</summary>
    Public Function MentionedFiles(content As String) As String
        Dim found As New List(Of String)
        For Each m As Match In Regex.Matches(If(content, ""), "(?:^|\s)@(?:""([^""]+)""|([^\s""]+))")
            Dim p = If(m.Groups(1).Success, m.Groups(1).Value, m.Groups(2).Value).TrimEnd("."c, ","c, ";"c, ":"c, "!"c, "?"c, ")"c)
            If p.Length > 0 AndAlso Not found.Contains(p) Then found.Add(p)
            If found.Count >= 6 Then Exit For
        Next
        Dim sb As New StringBuilder()
        For Each p In found
            Try
                Dim full = Resolve(p)
                If Directory.Exists(full) Then
                    sb.Append(vbLf & $"Folder the user mentioned: @{p}" & vbLf).Append(ListDir(full))
                ElseIf File.Exists(full) Then
                    Dim bytes = File.ReadAllBytes(full)
                    If bytes.Take(8000).Contains(CByte(0)) Then
                        sb.Append(vbLf & $"File the user mentioned: @{p} ({full}): binary, {SizeText(bytes.Length)}." & vbLf)
                        Continue For
                    End If
                    Dim text = TextFileData.Load(full).Text
                    If text.Length > 60000 Then
                        text = text.Substring(0, 60000) & vbLf & "… (shortened; read_file for more)"
                    Else
                        ' The model has the whole file: it may change it without reading it again (like Claude Code's @ mentions).
                        NoteRead(MentionedKey, full)
                    End If
                    sb.Append(vbLf & $"File the user mentioned: @{p} ({full})" & vbLf & "```" & vbLf).Append(text).Append(vbLf & "```" & vbLf)
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
                ' Not a file in the workspace: the model reads it as plain text.
            End Try
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Files for the @ list in the message box: paths (relative to the first workspace folder) containing the typed text.</summary>
    Public Function ListFiles(query As String) As List(Of String)
        Dim roots = _settings.EffectiveWorkspaces()
        Dim q = If(query, "").Trim().Replace("\", "/")
        Dim hits As New List(Of String)
        Dim seen = 0
        For Each root In roots
            For Each f In WalkFiles(root)
                seen += 1
                If seen > 30000 Then Exit For
                Dim rel = If(root = roots(0), IO.Path.GetRelativePath(root, f), f).Replace("\", "/")
                If q.Length = 0 OrElse rel.Contains(q, StringComparison.OrdinalIgnoreCase) Then hits.Add(rel)
                If hits.Count >= 400 Then Exit For
            Next
        Next
        ' Names that match come first, then shorter paths.
        Return hits.OrderBy(Function(h) If(q.Length > 0 AndAlso IO.Path.GetFileName(h).Contains(q, StringComparison.OrdinalIgnoreCase), 0, 1)).ThenBy(Function(h) h.Length).Take(30).ToList()
    End Function

    ''' <summary>/add-dir: adds a workspace folder (the caller saves the settings). Returns its full path.</summary>
    Public Function AddWorkspaceFolder(folder As String) As String
        Dim p = If(folder, "").Trim().Trim(""""c)
        If p.StartsWith("~") Then p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) & p.Substring(1)
        If p.Length = 0 Then Throw New ArgumentException("Give a folder.")
        Dim full = IO.Path.GetFullPath(p)
        If Not Directory.Exists(full) Then Throw New ArgumentException("No such folder: " & full)
        Dim current = _settings.EffectiveWorkspaces()
        If current.Any(Function(r) String.Equals(r.TrimEnd("\"c, "/"c), full.TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase)) Then Return full
        ' No folders set means "the home folder": keep it, and add the new one.
        If Not _settings.WorkspaceFolders.Any(Function(f) Not String.IsNullOrWhiteSpace(f) AndAlso Directory.Exists(f)) Then _settings.WorkspaceFolders = current
        _settings.WorkspaceFolders.Add(full)
        Return full
    End Function

End Class
