Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading

''' <summary>
''' Claude Code (the claude CLI) on this computer, answering in the Chat tab. Each message runs "claude -p" in its streaming
''' JSON mode and continues the chat's Claude session (--resume), so Claude keeps its own memory of the conversation, its
''' tools, MCP servers, skills, slash commands, CLAUDE.md files and settings. Its events go to the page unchanged; permission
''' prompts (control_request can_use_tool) are answered by the page through Respond, like the official SDK does.
''' </summary>
Public NotInheritable Class ClaudeCode

    Private NotInheritable Class Run
        Public Property Process As Process
        Public ReadOnly Gate As New SemaphoreSlim(1, 1)
        Public Property Done As Boolean
    End Class

    Private ReadOnly _runs As New ConcurrentDictionary(Of String, Run)(StringComparer.Ordinal)

    ''' <summary>The claude program: on PATH (the npm or native installer), the native installer's folder, or the copy inside the VS Code extension.</summary>
    Public Shared Function Find() As (Path As String, Source As String)
        Dim onPath = PlatformUtil.Which("claude")
        If onPath IsNot Nothing Then Return (onPath, "PATH")
        Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        For Each p In {IO.Path.Combine(home, ".local", "bin", If(OperatingSystem.IsWindows(), "claude.exe", "claude")),
                       IO.Path.Combine(home, ".claude", "local", If(OperatingSystem.IsWindows(), "claude.exe", "claude"))}
            If File.Exists(p) Then Return (p, "native installer")
        Next
        For Each extRoot In {IO.Path.Combine(home, ".vscode", "extensions"), IO.Path.Combine(home, ".cursor", "extensions")}
            If Not Directory.Exists(extRoot) Then Continue For
            Dim newest = Directory.GetDirectories(extRoot, "anthropic.claude-code-*").OrderByDescending(Function(d) d, StringComparer.OrdinalIgnoreCase).
                Select(Function(d) IO.Path.Combine(d, "resources", "native-binary", If(OperatingSystem.IsWindows(), "claude.exe", "claude"))).FirstOrDefault(AddressOf File.Exists)
            If newest IsNot Nothing Then Return (newest, "VS Code extension")
        Next
        Return (Nothing, Nothing)
    End Function

    ''' <summary>How to start it: an npm .cmd shim is run through its cli.js with node (cmd.exe would mangle the arguments).</summary>
    Private Shared Function StartInfo(exe As String) As ProcessStartInfo
        If exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) OrElse exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) Then
            Dim dir = IO.Path.GetDirectoryName(exe)
            Dim cli = IO.Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js")
            Dim bin = IO.Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe")
            If File.Exists(bin) Then Return New ProcessStartInfo(bin)
            If File.Exists(cli) Then
                Dim psi As New ProcessStartInfo(If(PlatformUtil.Which("node"), "node"))
                psi.ArgumentList.Add(cli)
                Return psi
            End If
            Dim viaCmd As New ProcessStartInfo("cmd.exe")
            viaCmd.ArgumentList.Add("/d") : viaCmd.ArgumentList.Add("/s") : viaCmd.ArgumentList.Add("/c") : viaCmd.ArgumentList.Add(exe)
            Return viaCmd
        End If
        Return New ProcessStartInfo(exe)
    End Function

    Public Shared Async Function StatusAsync() As Task(Of Object)
        Dim found = Find()
        If found.Path Is Nothing Then Return New With {.installed = False}
        Dim version As String = Nothing
        Try
            Dim psi = StartInfo(found.Path)
            psi.ArgumentList.Add("--version")
            psi.RedirectStandardOutput = True : psi.RedirectStandardError = True : psi.UseShellExecute = False : psi.CreateNoWindow = True
            Using p = Process.Start(psi)
                Dim read = p.StandardOutput.ReadToEndAsync()
                If Await Task.WhenAny(read, Task.Delay(15000)).ConfigureAwait(False) Is read Then version = (Await read.ConfigureAwait(False)).Trim()
                If Not p.HasExited Then p.Kill(True)
            End Using
        Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return New With {.installed = False, .path = found.Path, .problem = ex.Message}
        End Try
        Return New With {.installed = True, .path = found.Path, .source = found.Source, .version = version}
    End Function

    ''' <summary>
    ''' Runs one message. body: {runId, prompt, images: [{mediaType, data}], files: [{name, data}], sessionId, plan,
    ''' settings: {model, permissionMode, effort, cwd, addDirs, appendSystemPrompt, maxTurns, fallbackModel, allowedTools,
    ''' disallowedTools, maxBudgetUsd}}. Each line Claude prints is passed to onLine; the task ends when Claude exits.
    ''' </summary>
    Public Async Function RunAsync(body As JsonElement, defaultFolder As String, onLine As Func(Of String, Task), ct As CancellationToken) As Task
        Dim found = Find()
        If found.Path Is Nothing Then Throw New InvalidOperationException("Claude Code is not installed on this computer (the claude command was not found).")
        Dim runId = Str(body, "runId")
        If String.IsNullOrWhiteSpace(runId) Then runId = Guid.NewGuid().ToString("N")
        Dim s As JsonElement
        Dim hasSettings = body.TryGetProperty("settings", s) AndAlso s.ValueKind = JsonValueKind.Object
        Dim cwd = If(hasSettings, Str(s, "cwd"), Nothing)
        If String.IsNullOrWhiteSpace(cwd) OrElse Not Directory.Exists(cwd) Then cwd = defaultFolder

        Dim psi = StartInfo(found.Path)
        AddArgs(psi, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--permission-prompt-tool", "stdio")
        Dim session = Str(body, "sessionId")
        Dim sessionGuid As Guid
        If Not String.IsNullOrWhiteSpace(session) AndAlso Guid.TryParse(session, sessionGuid) Then AddArgs(psi, "--resume", session)
        Dim planEl As JsonElement
        Dim plan = body.TryGetProperty("plan", planEl) AndAlso planEl.ValueKind = JsonValueKind.True
        If hasSettings Then
            Dim model = Str(s, "model")
            If Not String.IsNullOrWhiteSpace(model) AndAlso model <> "default" Then AddArgs(psi, "--model", model)
            Dim mode = If(plan, "plan", Str(s, "permissionMode"))
            If Not String.IsNullOrWhiteSpace(mode) AndAlso mode <> "default" Then AddArgs(psi, "--permission-mode", mode)
            Dim effort = Str(s, "effort")
            If Not String.IsNullOrWhiteSpace(effort) AndAlso effort <> "default" Then AddArgs(psi, "--effort", effort)
            For Each d In List(s, "addDirs").Where(AddressOf Directory.Exists)
                AddArgs(psi, "--add-dir", d)
            Next
            Dim append = Str(s, "appendSystemPrompt")
            If Not String.IsNullOrWhiteSpace(append) Then AddArgs(psi, "--append-system-prompt", append)
            Dim turns = Str(s, "maxTurns")
            Dim turnCount As Integer
            If Integer.TryParse(turns, turnCount) AndAlso turnCount > 0 Then AddArgs(psi, "--max-turns", turnCount.ToString(Globalization.CultureInfo.InvariantCulture))
            Dim fallback = Str(s, "fallbackModel")
            If Not String.IsNullOrWhiteSpace(fallback) Then AddArgs(psi, "--fallback-model", fallback)
            Dim budget = Str(s, "maxBudgetUsd")
            Dim budgetUsd As Double
            If Double.TryParse(budget, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, budgetUsd) AndAlso budgetUsd > 0 Then AddArgs(psi, "--max-budget-usd", budgetUsd.ToString(Globalization.CultureInfo.InvariantCulture))
            Dim allowed = List(s, "allowedTools")
            If allowed.Count > 0 Then AddArgs(psi, "--allowedTools", String.Join(",", allowed))
            Dim denied = List(s, "disallowedTools")
            If denied.Count > 0 Then AddArgs(psi, "--disallowedTools", String.Join(",", denied))
        ElseIf plan Then
            AddArgs(psi, "--permission-mode", "plan")
        End If
        psi.WorkingDirectory = cwd
        psi.RedirectStandardInput = True : psi.RedirectStandardOutput = True : psi.RedirectStandardError = True
        psi.UseShellExecute = False : psi.CreateNoWindow = True
        psi.StandardOutputEncoding = Encoding.UTF8 : psi.StandardErrorEncoding = Encoding.UTF8
        ' Started from inside another Claude Code session (a developer's terminal), claude would refuse to run.
        psi.Environment.Remove("CLAUDECODE")
        psi.Environment("CLAUDE_CODE_ENTRYPOINT") = "agentic-desktop"

        ' The message: text, pictures, and attached files saved where Claude can read them.
        Dim content As New JsonArray()
        Dim prompt = If(Str(body, "prompt"), "")
        Dim files As JsonElement
        If body.TryGetProperty("files", files) AndAlso files.ValueKind = JsonValueKind.Array AndAlso files.GetArrayLength() > 0 Then
            Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-claude", runId)
            Directory.CreateDirectory(dir)
            Dim saved As New List(Of String)
            For Each f In files.EnumerateArray().Take(10)
                Dim name = IO.Path.GetFileName(If(Str(f, "name"), "file"))
                If String.IsNullOrWhiteSpace(name) Then name = "file"
                Dim target = IO.Path.Combine(dir, name)
                Await File.WriteAllBytesAsync(target, Convert.FromBase64String(If(Str(f, "data"), "")), ct).ConfigureAwait(False)
                saved.Add(target)
            Next
            prompt &= vbLf & vbLf & "Attached files (saved on this computer):" & vbLf & String.Join(vbLf, saved.Select(Function(x) "- " & x))
        End If
        Dim images As JsonElement
        If body.TryGetProperty("images", images) AndAlso images.ValueKind = JsonValueKind.Array Then
            For Each img In images.EnumerateArray().Take(10)
                content.Add(New JsonObject From {{"type", "image"}, {"source", New JsonObject From {{"type", "base64"}, {"media_type", If(Str(img, "mediaType"), "image/png")}, {"data", Str(img, "data")}}}})
            Next
        End If
        content.Add(New JsonObject From {{"type", "text"}, {"text", prompt}})

        Dim r As New Run()
        Dim p As Process
        Try
            p = Process.Start(psi)
        Catch ex As ComponentModel.Win32Exception
            Throw New InvalidOperationException("Could not start Claude Code: " & ex.Message)
        End Try
        r.Process = p
        _runs(runId) = r
        Dim stderr As New StringBuilder()
        AddHandler p.ErrorDataReceived, Sub(sender, e)
                                            If e.Data Is Nothing Then Return
                                            SyncLock stderr
                                                If stderr.Length < 20000 Then stderr.AppendLine(e.Data)
                                            End SyncLock
                                        End Sub
        p.BeginErrorReadLine()
        Using reg = ct.Register(Sub() Kill(r))
            Try
                Await WriteLineAsync(r, New JsonObject From {{"type", "user"}, {"message", New JsonObject From {{"role", "user"}, {"content", content}}}}.ToJsonString()).ConfigureAwait(False)
                Await onLine(New JsonObject From {{"type", "agentic_started"}, {"runId", runId}, {"cwd", cwd}, {"source", found.Source}}.ToJsonString()).ConfigureAwait(False)
                While True
                    Dim line = Await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(False)
                    If line Is Nothing Then Exit While
                    If line.Length = 0 Then Continue While
                    ' The answer is complete: close stdin so claude exits (it waits for more messages otherwise).
                    If IsResult(line) Then
                        r.Done = True
                        Await onLine(line).ConfigureAwait(False)
                        Try
                            p.StandardInput.Close()
                        Catch ex As IOException
                        End Try
                        Continue While
                    End If
                    Await onLine(line).ConfigureAwait(False)
                End While
                Await p.WaitForExitAsync(ct).ConfigureAwait(False)
                If Not r.Done Then
                    Dim err As String
                    SyncLock stderr
                        err = stderr.ToString().Trim()
                    End SyncLock
                    Await onLine(New JsonObject From {{"type", "agentic_error"}, {"exitCode", p.ExitCode},
                                 {"message", If(err.Length > 0, err.Substring(Math.Max(0, err.Length - 3000)), $"Claude Code stopped (exit code {p.ExitCode}).")}}.ToJsonString()).ConfigureAwait(False)
                End If
            Finally
                Kill(r)
                Dim gone As Run = Nothing
                _runs.TryRemove(runId, gone)
                p.Dispose()
            End Try
        End Using
    End Function

    ''' <summary>The page answers a control request (a permission prompt): {runId, requestId, response}.</summary>
    Public Async Function RespondAsync(body As JsonElement) As Task(Of Boolean)
        Dim r As Run = Nothing
        If Not _runs.TryGetValue(If(Str(body, "runId"), ""), r) Then Return False
        Dim response As JsonElement
        If Not body.TryGetProperty("response", response) Then Throw New ArgumentException("response is required.")
        Dim msg As New JsonObject From {{"type", "control_response"}, {"response", New JsonObject From {
            {"subtype", "success"}, {"request_id", Str(body, "requestId")}, {"response", JsonNode.Parse(response.GetRawText())}}}}
        Await WriteLineAsync(r, msg.ToJsonString()).ConfigureAwait(False)
        Return True
    End Function

    ''' <summary>Stops the answer: Claude is asked to interrupt (it ends the turn cleanly), and stopped hard if it does not.</summary>
    Public Async Function InterruptAsync(runId As String) As Task(Of Boolean)
        Dim r As Run = Nothing
        If Not _runs.TryGetValue(If(runId, ""), r) Then Return False
        Try
            Await WriteLineAsync(r, New JsonObject From {{"type", "control_request"}, {"request_id", "int_" & Guid.NewGuid().ToString("N")},
                                                          {"request", New JsonObject From {{"subtype", "interrupt"}}}}.ToJsonString()).ConfigureAwait(False)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
        End Try
        Dim unused = Task.Delay(5000).ContinueWith(Sub(t) Kill(r))
        Return True
    End Function

    Private Shared Sub AddArgs(psi As ProcessStartInfo, ParamArray values As String())
        For Each v In values
            psi.ArgumentList.Add(v)
        Next
    End Sub

    ''' <summary>The line that ends an answer: {"type":"result", …} (its keys come in any order).</summary>
    Friend Shared Function IsResult(line As String) As Boolean
        If line.IndexOf("""result""", StringComparison.Ordinal) < 0 Then Return False
        Try
            Using doc = JsonDocument.Parse(line)
                Dim t As JsonElement
                Return doc.RootElement.ValueKind = JsonValueKind.Object AndAlso doc.RootElement.TryGetProperty("type", t) AndAlso t.ValueKind = JsonValueKind.String AndAlso t.GetString() = "result"
            End Using
        Catch ex As JsonException
            Return False
        End Try
    End Function

    Private Shared Async Function WriteLineAsync(r As Run, line As String) As Task
        Await r.Gate.WaitAsync().ConfigureAwait(False)
        Try
            Await r.Process.StandardInput.WriteLineAsync(line).ConfigureAwait(False)
            Await r.Process.StandardInput.FlushAsync().ConfigureAwait(False)
        Finally
            r.Gate.Release()
        End Try
    End Function

    Private Shared Sub Kill(r As Run)
        Try
            If r.Process IsNot Nothing AndAlso Not r.Process.HasExited Then r.Process.Kill(True)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
        End Try
    End Sub

    Private Shared Function Str(e As JsonElement, name As String) As String
        Dim v As JsonElement
        If e.ValueKind <> JsonValueKind.Object OrElse Not e.TryGetProperty(name, v) Then Return Nothing
        Return If(v.ValueKind = JsonValueKind.String, v.GetString(), If(v.ValueKind = JsonValueKind.Number OrElse v.ValueKind = JsonValueKind.True OrElse v.ValueKind = JsonValueKind.False, v.GetRawText(), Nothing))
    End Function

    Private Shared Function List(e As JsonElement, name As String) As List(Of String)
        Dim v As JsonElement
        If e.ValueKind <> JsonValueKind.Object OrElse Not e.TryGetProperty(name, v) Then Return New List(Of String)
        If v.ValueKind = JsonValueKind.String Then Return v.GetString().Split({","c, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries).ToList()
        If v.ValueKind <> JsonValueKind.Array Then Return New List(Of String)
        Return v.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString().Trim()).Where(Function(x) x.Length > 0).Take(100).ToList()
    End Function
End Class
