Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading

' The hooks editor of /hooks in the chat: add a hook (Claude Code's format) to the project's or your own
' .agentic/settings.json, remove one, and try a command with a sample input to see its exit code and what it prints.

Partial Public Class LocalTools

    Private Shared ReadOnly HookJson As New JsonSerializerOptions With {.WriteIndented = True}

    ''' <summary>The file a new hook goes to: the first workspace folder's .agentic/settings.json, or ~/.agentic/settings.json.</summary>
    Private Function HookFileFor(scope As String) As String
        If scope = "user" Then Return IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentic", "settings.json")
        Return IO.Path.Combine(_settings.EffectiveWorkspaces()(0), ".agentic", "settings.json")
    End Function

    Private Shared Function ReadSettings(file As String) As JsonObject
        Dim text = PathsUtil.ReadFileSafe(file)
        If String.IsNullOrWhiteSpace(text) Then Return New JsonObject()
        Return If(TryCast(JsonNode.Parse(text, documentOptions:=New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True}), JsonObject), New JsonObject())
    End Function

    Public Function SaveHook(scope As String, eventName As String, matcher As String, command As String, timeout As Integer) As String
        If Not HookEvents.Contains(eventName) Then Throw New ArgumentException("Unknown hook event: " & eventName)
        If String.IsNullOrWhiteSpace(command) Then Throw New ArgumentException("Give the command the hook runs.")
        Dim file = HookFileFor(scope)
        Dim root = ReadSettings(file)
        Dim hooks = TryCast(root("hooks"), JsonObject)
        If hooks Is Nothing Then
            hooks = New JsonObject()
            root("hooks") = hooks
        End If
        Dim groups = TryCast(hooks(eventName), JsonArray)
        If groups Is Nothing Then
            groups = New JsonArray()
            hooks(eventName) = groups
        End If
        Dim entry As New JsonObject From {{"type", "command"}, {"command", command.Trim()}}
        If timeout > 0 Then entry("timeout") = timeout
        Dim group As New JsonObject From {{"hooks", New JsonArray(entry)}}
        If Not String.IsNullOrWhiteSpace(matcher) Then group("matcher") = matcher.Trim()
        groups.Add(group)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(file))
        IO.File.WriteAllText(file, root.ToJsonString(HookJson))
        Return file
    End Function

    ''' <summary>Removes the first hook of that event, matcher and command from one of the hook files.</summary>
    Public Function RemoveHook(file As String, eventName As String, matcher As String, command As String) As Boolean
        If Not HookFiles().Any(Function(f) String.Equals(IO.Path.GetFullPath(f), IO.Path.GetFullPath(If(file, "")), StringComparison.OrdinalIgnoreCase)) Then
            Throw New ArgumentException("Hooks are removed only from the settings files the chat uses.")
        End If
        Dim root = ReadSettings(file)
        Dim groups = TryCast(TryCast(root("hooks"), JsonObject)?(eventName), JsonArray)
        If groups Is Nothing Then Return False
        For Each g In groups.OfType(Of JsonObject)().ToList()
            If Not String.Equals(If(g("matcher")?.ToString(), ""), If(matcher, ""), StringComparison.Ordinal) Then Continue For
            Dim list = TryCast(g("hooks"), JsonArray)
            Dim h = list?.OfType(Of JsonObject)().FirstOrDefault(Function(x) x("command")?.ToString() = command)
            If h Is Nothing Then Continue For
            list.Remove(h)
            If list.Count = 0 Then groups.Remove(g)
            IO.File.WriteAllText(file, root.ToJsonString(HookJson))
            Return True
        Next
        Return False
    End Function

    ''' <summary>Runs a hook command once with a sample input of that event: its exit code and what it printed.</summary>
    Public Async Function TestHookAsync(eventName As String, command As String, ct As CancellationToken) As Task(Of Object)
        Dim sample As New JsonObject From {{"session_id", "test"}, {"hook_event_name", eventName}, {"cwd", _settings.EffectiveWorkspaces()(0)}, {"permission_mode", "default"}}
        If ToolEvents.Contains(eventName) Then
            sample("tool_name") = "Bash"
            sample("tool_input") = New JsonObject From {{"command", "echo hello"}}
        End If
        If eventName = "UserPromptSubmit" Then sample("prompt") = "Hello"
        Dim psi = ShellStartInfo(command, _settings.EffectiveWorkspaces()(0))
        psi.RedirectStandardInput = True
        psi.Environment("CLAUDE_PROJECT_DIR") = _settings.EffectiveWorkspaces()(0)
        Using proc As New Process With {.StartInfo = psi}
            proc.Start()
            Dim outTask = proc.StandardOutput.ReadToEndAsync()
            Dim errTask = proc.StandardError.ReadToEndAsync()
            Try
                Await proc.StandardInput.WriteAsync(sample.ToJsonString()).ConfigureAwait(False)
                proc.StandardInput.Close()
            Catch ex As IOException
            End Try
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(TimeSpan.FromSeconds(30))
                Try
                    Await proc.WaitForExitAsync(cts.Token).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Try
                        proc.Kill(True)
                    Catch
                    End Try
                    Return New With {.code = -1, .stdout = "", .stderr = "It took longer than 30 s and was stopped.", .input = sample.ToJsonString()}
                End Try
            End Using
            Return New With {.code = proc.ExitCode, .stdout = (Await outTask.ConfigureAwait(False)).Trim(), .stderr = (Await errTask.ConfigureAwait(False)).Trim(), .input = sample.ToJsonString()}
        End Using
    End Function

End Class
