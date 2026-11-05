Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading

''' <summary>
''' Tools the chat model can use on this computer, like Claude Code: read, list, search, write and edit files inside the
''' workspace folders, run shell commands, and call the Agentic API as you. Changes and commands need your approval in
''' "ask" mode (the desktop layer asks before it calls Run); "off" disables them.
''' </summary>
Partial Public Class LocalTools

    Private ReadOnly _settings As AppSettings
    Private Shared ReadOnly Http As New HttpClient With {.Timeout = TimeSpan.FromSeconds(120)}
    Private Const MaxOutput As Integer = 60000

    Public Sub New(settings As AppSettings)
        _settings = settings
    End Sub

    ''' <summary>Tools that change something (they need approval in "ask" mode).</summary>
    Public Shared ReadOnly ChangingTools As New HashSet(Of String) From {"write_file", "edit_file", "run_command", "run_command_background", "kill_command", "edit_notebook", "monitor"}

    Public Function Definitions() As List(Of JsonObject)
        Dim ws = String.Join("; ", _settings.EffectiveWorkspaces())
        Dim list As New List(Of JsonObject) From {
            Def("read_file", $"Read a file on the user's computer (inside the workspace: {ws}). Returns numbered lines; use offset/limit for big files. " &
                "A picture (PNG, JPEG, WebP) comes back as an image for you to look at. Read a file before you edit_file or write_file it.",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string""},""offset"":{""type"":""integer"",""description"":""first line (1-based)""},""limit"":{""type"":""integer""}},""required"":[""path""]}"),
            Def("list_dir", "List a folder on the user's computer (files with sizes, sub-folders).",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""folder; empty = the workspace""}}}"),
            Def("glob_files", "Find files by name pattern, e.g. **/*.cs or src/**/test_*.py.",
                "{""type"":""object"",""properties"":{""pattern"":{""type"":""string""},""path"":{""type"":""string""}},""required"":[""pattern""]}"),
            Def("grep", "Search file contents with a regular expression; returns path:line: text.",
                "{""type"":""object"",""properties"":{""pattern"":{""type"":""string""},""path"":{""type"":""string""},""glob"":{""type"":""string"",""description"":""only files matching, e.g. *.vb""},""ignore_case"":{""type"":""boolean""}},""required"":[""pattern""]}"),
            Def("write_file", "Create or overwrite a text file (the user approves it). To overwrite an existing file, read it with read_file first in this chat; " &
                "it fails when the file changed since you read it. Its encoding and line endings are kept.",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string""},""content"":{""type"":""string""}},""required"":[""path"",""content""]}"),
            Def("edit_file", "Replace exact text in a file (old_string must match exactly and be unique unless replace_all). The user approves it. " &
                "Read the file with read_file first in this chat; the edit fails when the file changed since you read it (read it again then).",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string""},""old_string"":{""type"":""string""},""new_string"":{""type"":""string""},""replace_all"":{""type"":""boolean""}},""required"":[""path"",""old_string"",""new_string""]}"),
            Def("run_command", $"Run a {ShellLabel()} command on the user's {If(OperatingSystem.IsWindows(), "Windows", "Linux")} computer and return its output (the user approves it). Starts in {_settings.EffectiveWorkspaces()(0)} unless cwd is given" &
                If(_settings.Shell = "cmd" AndAlso OperatingSystem.IsWindows(), ". ", "; a cd carries over to the chat's later commands (inside the workspace folders). ") &
                "It waits up to wait_seconds (default 120, at most 3600) for the command. One still running then is not stopped: it goes on in the background and " &
                "you get its id and output so far - check on it with command_output, stop it with kill_command. For something that keeps running (a dev server, a " &
                "watcher) or may take long, use run_command_background.",
                "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""cwd"":{""type"":""string""},""wait_seconds"":{""type"":""integer"",""description"":""How long to wait for it before it goes on in the background (default 120)""}" &
                If(OperatingSystem.IsWindows(), ",""elevated"":{""type"":""boolean"",""description"":""Run it as administrator in Windows PowerShell (the user confirms Windows' admin prompt, and the chat asks first every time). Only when the command needs admin rights.""}", "") &
                "},""required"":[""command""]}"),
            Def("agentic_api", "Call the Agentic server's REST API as the user (same permissions as the dashboard): agents, tasks, global tasks, results, finance, models… Reading (GET) is free; changes need the user's approval. Paths start with /api/.",
                "{""type"":""object"",""properties"":{""method"":{""type"":""string"",""enum"":[""GET"",""POST"",""PUT"",""PATCH"",""DELETE""]},""path"":{""type"":""string""},""body"":{""type"":""object""}},""required"":[""method"",""path""]}")}
        list.AddRange(ExtraDefinitions())
        list.Add(FetchDefinition())
        list.AddRange(RunDefinitions())
        Return list
    End Function

    ''' <summary>A tool definition with its risk for the chat's permission mode: read | change | conditional (agentic_api: GET is read; preview: start runs the project).</summary>
    Private Shared Function Def(name As String, description As String, schema As String) As JsonObject
        Dim risk = If(ChangingTools.Contains(name), "change", If(name = "agentic_api" OrElse name = "preview", "conditional", "read"))
        Return New JsonObject From {{"name", name}, {"description", description}, {"parameters", JsonNode.Parse(schema)}, {"x-risk", risk}}
    End Function

    ''' <summary>Why a call needs approval (Nothing = it does not).</summary>
    Public Function ApprovalReason(name As String, args As JsonElement) As String
        If _settings.LocalToolsMode = "auto" Then Return Nothing
        If ChangingTools.Contains(name) Then Return name.Replace("_", " ")
        If name = "agentic_api" AndAlso Not Str(args, "method", "GET").Equals("GET", StringComparison.OrdinalIgnoreCase) Then Return "change on the Agentic server"
        If name = "preview" AndAlso Str(args, "action", "status").Equals("start", StringComparison.OrdinalIgnoreCase) Then Return "run the project"
        If name = "preview" AndAlso Str(args, "action", "status").Equals("share", StringComparison.OrdinalIgnoreCase) Then Return "share the project's website with a public link"
        Return Nothing
    End Function

    ''' <summary>Saves files before an answer's first change to them (Rewind in the chat puts them back).</summary>
    Public Property Checkpoints As FileCheckpoints

    ''' <summary>The project's instructions file: AGENTIC.md (or an existing agentic.md / AGENTS.md) in the first workspace folder.</summary>
    Public Function AgenticMdPath() As String
        Dim folder = _settings.EffectiveWorkspaces()(0)
        For Each fileName In {"AGENTIC.md", "agentic.md", "AGENTS.md"}
            Dim p = IO.Path.Combine(folder, fileName)
            If File.Exists(p) Then Return p
        Next
        Return IO.Path.Combine(folder, "AGENTIC.md")
    End Function

    ''' <summary>Project instruction files, in the order they are read (all that exist; the same text only once).</summary>
    Private Shared ReadOnly InstructionFiles As String() = {"AGENTIC.md", "agentic.md", "AGENTS.md", "CLAUDE.md", IO.Path.Combine(".claude", "CLAUDE.md"), "AGENTS.local.md", "CLAUDE.local.md"}

    ''' <summary>
    ''' The instruction files of every workspace folder (AGENTIC.md, AGENTS.md, CLAUDE.md and their local forms), for the model's
    ''' instructions ("" when there are none). Repositories often have only a CLAUDE.md, so it counts as much as the others.
    ''' </summary>
    Public Function AgenticInstructions() As String
        Dim sb As New StringBuilder()
        Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim seenTexts As New HashSet(Of String)(StringComparer.Ordinal)
        For Each folder In _settings.EffectiveWorkspaces()
            For Each fileName In InstructionFiles
                Dim p = IO.Path.Combine(folder, fileName)
                If Not File.Exists(p) Then Continue For
                Try
                    ' Windows: AGENTIC.md and agentic.md are one file.
                    If Not seenPaths.Add(IO.Path.GetFullPath(p)) Then Continue For
                    Dim text = File.ReadAllText(p).Trim()
                    If text.Length = 0 OrElse Not seenTexts.Add(text) Then Continue For
                    If text.Length > 20000 Then text = text.Substring(0, 20000) & vbLf & "… (shortened)"
                    If sb.Length + text.Length > 60000 Then Exit For
                    sb.Append(vbLf & $"Project instructions from {p} (follow them):" & vbLf).Append(text).Append(vbLf)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
        Next
        Return sb.ToString()
    End Function

    Private Async Function RunToolAsync(name As String, args As JsonElement, ct As CancellationToken, Optional chatId As String = Nothing) As Task(Of (Ok As Boolean, Text As String))
        If _settings.LocalToolsMode = "off" Then Return (False, "Tools on this computer are switched off in the desktop settings.")
        Try
            Select Case name
                Case "read_file"
                    Dim target = Resolve(Str(args, "path", ""))
                    Dim text = ReadFile(target, Int(args, "offset", 1), Int(args, "limit", 2000))
                    NoteRead(chatId, target)
                    Return (True, text)
                Case "list_dir" : Return (True, ListDir(Resolve(Str(args, "path", ""))))
                Case "glob_files" : Return (True, GlobFiles(Str(args, "pattern", "*"), Resolve(Str(args, "path", ""))))
                Case "grep" : Return (True, Grep(Str(args, "pattern", ""), Resolve(Str(args, "path", "")), Str(args, "glob", Nothing), Bool(args, "ignore_case")))
                Case "write_file"
                    Dim target = Resolve(Str(args, "path", ""))
                    Dim stale = StaleReason(chatId, target)
                    If stale IsNot Nothing Then Return (False, stale)
                    Checkpoints?.BeforeChange(chatId, target)
                    Dim said = WriteFile(target, Str(args, "content", ""))
                    NoteRead(chatId, target)
                    Return (True, said)
                Case "edit_file"
                    Dim target = Resolve(Str(args, "path", ""))
                    Dim stale = StaleReason(chatId, target)
                    If stale IsNot Nothing Then Return (False, stale)
                    Checkpoints?.BeforeChange(chatId, target)
                    Dim r = EditFile(target, Str(args, "old_string", ""), Str(args, "new_string", ""), Bool(args, "replace_all"))
                    If r.Item1 Then NoteRead(chatId, target)
                    Return r
                Case "run_command"
                    ' elevated: as administrator (Windows asks the user; the chat asked first).
                    If OperatingSystem.IsWindows() AndAlso Bool(args, "elevated") Then
                        Return Await RunElevatedAsync(Str(args, "command", ""), Str(args, "cwd", Nothing), Int(args, "wait_seconds", _settings.CommandTimeoutSeconds), ct, chatId).ConfigureAwait(False)
                    End If
                    Return Await RunCommandAsync(Str(args, "command", ""), Str(args, "cwd", Nothing), Int(args, "wait_seconds", Int(args, "timeout_seconds", _settings.CommandTimeoutSeconds)), ct, chatId).ConfigureAwait(False)
                Case "agentic_api" : Return Await CallAgenticAsync(Str(args, "method", "GET"), Str(args, "path", ""), args, ct).ConfigureAwait(False)
            End Select
            Return (False, $"Unknown tool {name}.")
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse
                                   TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is NotSupportedException
            Return (False, ex.Message)
        End Try
    End Function

    ' ------------------------------------------------------------------ files

    ''' <summary>Absolute path inside a workspace folder; relative paths start in the first one.</summary>
    Private Function Resolve(path As String) As String
        Dim roots = _settings.EffectiveWorkspaces()
        Dim full = If(String.IsNullOrWhiteSpace(path), roots(0), IO.Path.GetFullPath(If(IO.Path.IsPathRooted(path), path, IO.Path.Combine(roots(0), path))))
        ' Outside the workspace folders (and the folders the settings add or the user allowed): the chat page asks the user.
        If Not MayUsePath(full, roots) Then Throw New OutsideWorkspaceException(full, roots)
        Return full
    End Function

    ''' <summary>A path the chat names, inside a workspace folder (relative paths start in the first one).</summary>
    Public Function WorkspacePath(path As String) As String
        If String.IsNullOrWhiteSpace(path) Then Throw New ArgumentException("No file was given.")
        Return Resolve(path)
    End Function

    Private Shared Function ReadFile(path As String, offset As Integer, limit As Integer) As String
        If Not File.Exists(path) Then Throw New FileNotFoundException("No such file: " & path)
        Dim size = New FileInfo(path).Length
        If size > 20_000_000 Then Throw New IOException("The file is larger than 20 MB.")
        If LooksBinary(path) Then Return $"{path} is a binary file ({SizeText(size)}); its content cannot be shown as text."
        Dim lines = TextFileData.Load(path).Text.Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
        If lines.Length > 0 AndAlso lines(lines.Length - 1).Length = 0 Then lines = lines.Take(lines.Length - 1).ToArray()
        offset = Math.Max(1, offset)
        limit = Math.Clamp(limit, 1, 5000)
        Dim sb As New StringBuilder()
        For i = offset - 1 To Math.Min(lines.Length, offset - 1 + limit) - 1
            sb.Append((i + 1).ToString().PadLeft(6)).Append(ControlChars.Tab).Append(If(lines(i).Length > 2000, lines(i).Substring(0, 2000) & "…", lines(i))).Append(vbLf)
            If sb.Length > MaxOutput Then sb.Append("… (truncated; use offset/limit)") : Exit For
        Next
        If lines.Length > offset - 1 + limit Then sb.Append($"… {lines.Length - (offset - 1 + limit)} more lines (use offset {offset + limit})")
        Return If(sb.Length = 0, "(empty file)", sb.ToString())
    End Function

    Private Shared Function ListDir(path As String) As String
        If Not Directory.Exists(path) Then Throw New DirectoryNotFoundException("No such folder: " & path)
        Dim d As New DirectoryInfo(path)
        Dim sb As New StringBuilder(path & vbLf)
        For Each sub1 In d.EnumerateDirectories().OrderBy(Function(x) x.Name).Take(300)
            sb.Append("  ").Append(sub1.Name).Append("/").Append(vbLf)
        Next
        For Each f In d.EnumerateFiles().OrderBy(Function(x) x.Name).Take(500)
            sb.Append("  ").Append(f.Name).Append("  (").Append(SizeText(f.Length)).Append(")").Append(vbLf)
        Next
        Return sb.ToString()
    End Function

    Private Shared ReadOnly SkipDirs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {".git", "node_modules", "bin", "obj", ".vs", "__pycache__", ".venv", "dist", "build"}

    Private Shared Iterator Function WalkFiles(root As String) As IEnumerable(Of String)
        Dim stack As New Stack(Of String)
        stack.Push(root)
        While stack.Count > 0
            Dim dir = stack.Pop()
            Dim files As String() = Array.Empty(Of String)()
            Dim dirs As String() = Array.Empty(Of String)()
            Try
                files = Directory.GetFiles(dir)
                dirs = Directory.GetDirectories(dir)
            Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IOException
            End Try
            For Each f In files
                Yield f
            Next
            For Each d In dirs.Reverse()
                If Not SkipDirs.Contains(IO.Path.GetFileName(d)) Then stack.Push(d)
            Next
        End While
    End Function

    Private Shared Function GlobToRegex(glob As String) As Regex
        Dim p = Regex.Escape(glob.Replace("\"c, "/"c)).Replace("\*\*/", "(.*/)?").Replace("\*\*", ".*").Replace("\*", "[^/]*").Replace("\?", "[^/]")
        Return New Regex("^" & p & "$", RegexOptions.IgnoreCase)
    End Function

    Private Shared Function GlobFiles(pattern As String, root As String) As String
        Dim re = GlobToRegex(If(pattern.Contains("/") OrElse pattern.Contains("\"), pattern, "**/" & pattern))
        Dim hits = WalkFiles(root).Select(Function(f) IO.Path.GetRelativePath(root, f).Replace("\"c, "/"c)).Where(Function(r) re.IsMatch(r)).Take(500).ToList()
        Return If(hits.Count = 0, "No files match.", root & vbLf & String.Join(vbLf, hits) & If(hits.Count = 500, vbLf & "… (first 500)", ""))
    End Function

    Private Shared Function Grep(pattern As String, root As String, glob As String, ignoreCase As Boolean) As String
        Dim re As New Regex(pattern, If(ignoreCase, RegexOptions.IgnoreCase, RegexOptions.None), TimeSpan.FromSeconds(2))
        Dim globRe = If(String.IsNullOrWhiteSpace(glob), Nothing, GlobToRegex(If(glob.Contains("/"), glob, "**/" & glob)))
        Dim sb As New StringBuilder()
        Dim count = 0
        Dim files = If(File.Exists(root), {root}.AsEnumerable(), WalkFiles(root))
        For Each f In files
            Dim rel = If(File.Exists(root), IO.Path.GetFileName(f), IO.Path.GetRelativePath(root, f).Replace("\"c, "/"c))
            If globRe IsNot Nothing AndAlso Not globRe.IsMatch(rel) Then Continue For
            Try
                If New FileInfo(f).Length > 5_000_000 Then Continue For
                Dim n = 0
                For Each line In File.ReadLines(f)
                    n += 1
                    If line.IndexOf(ChrW(0)) >= 0 Then Exit For ' binary
                    If re.IsMatch(line) Then
                        sb.Append(rel).Append(":").Append(n).Append(": ").Append(If(line.Length > 300, line.Substring(0, 300) & "…", line.Trim())).Append(vbLf)
                        count += 1
                        If count >= 300 OrElse sb.Length > MaxOutput Then Return sb.Append("… (stopped at 300 matches)").ToString()
                    End If
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is RegexMatchTimeoutException
            End Try
        Next
        Return If(count = 0, "No matches.", sb.ToString())
    End Function

    ''' <summary>Overwriting a file keeps its encoding, byte order mark and line endings (unchanged lines keep theirs).</summary>
    Private Shared Function WriteFile(path As String, content As String) As String
        Dim existing = TextFileData.Load(path)
        Dim body = If(existing.Exists, LineEndings.Restore(existing.Text, content), content)
        existing.Save(path, body)
        Return $"{If(existing.Exists, "Overwrote", "Created")} {path} ({SizeText(existing.Enc.GetByteCount(body))})."
    End Function

    Private Shared Function EditFile(path As String, oldText As String, newText As String, replaceAll As Boolean) As (Boolean, String)
        If Not File.Exists(path) Then Return (False, "No such file: " & path)
        ' The file keeps its encoding and byte order mark (UTF-8, UTF-16, ANSI) and its line endings.
        Dim tf = TextFileData.Load(path)
        Dim r = EditMatch.Apply(tf.Text, oldText, newText, replaceAll)
        If Not r.Ok Then Return (False, r.Message)
        If r.Replaced = 0 Then Return (True, $"{path}: {r.Message}")
        tf.Save(path, r.Text)
        Return (True, $"Edited {path} ({r.Replaced} replacement{If(r.Replaced > 1, "s", "")}).{If(String.IsNullOrEmpty(r.Message), "", " " & r.Message)}")
    End Function

    ' ------------------------------------------------------------------ commands

    ''' <summary>The shells run_command can use on this platform (the first is the default).</summary>
    Public Shared Function Shells() As String()
        Return If(OperatingSystem.IsWindows(), {"powershell", "cmd"}, {"bash", "sh"})
    End Function

    Private Function ShellLabel() As String
        Select Case _settings.Shell
            Case "cmd" : Return "cmd.exe"
            Case "bash" : Return "bash"
            Case "sh" : Return "POSIX sh"
            Case Else : Return "PowerShell"
        End Select
    End Function

    ''' <summary>
    ''' run_command: a command is never stopped for taking long. It waits up to waitSeconds for it, then lets it go on in the
    ''' background, so the model gets control back and can audit it (command_output) or stop it (kill_command) instead of
    ''' being stuck behind it. It also returns as soon as the shell ends, even when something the command started (a server)
    ''' keeps its output open - waiting for that output to end is what used to hang.
    ''' </summary>
    ''' <summary>
    ''' A command run as administrator (Windows): a script in a folder of its own runs in an elevated Windows PowerShell (Windows
    ''' asks the user), writing its output and exit code to files this app reads afterwards.
    ''' </summary>
    Private Async Function RunElevatedAsync(command As String, cwd As String, waitSeconds As Integer, ct As CancellationToken, Optional chatId As String = Nothing) As Task(Of (Boolean, String))
        If String.IsNullOrWhiteSpace(command) Then Return (False, "command is required.")
        Dim folder = CommandFolder(chatId, cwd)
        Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-admin-" & Guid.NewGuid().ToString("N").Substring(0, 10))
        IO.Directory.CreateDirectory(dir)
        Dim script = IO.Path.Combine(dir, "run.ps1"), outFile = IO.Path.Combine(dir, "out.txt"), codeFile = IO.Path.Combine(dir, "code.txt")
        Dim q = Function(s As String) "'" & s.Replace("'", "''") & "'"
        IO.File.WriteAllText(script, "$ErrorActionPreference = 'Continue'" & vbCrLf &
                                     "Set-Location -LiteralPath " & q(folder) & vbCrLf &
                                     "& {" & vbCrLf & command & vbCrLf & "} *>&1 | Out-File -FilePath " & q(outFile) & " -Encoding utf8 -Width 4096" & vbCrLf &
                                     "$code = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } elseif ($?) { 0 } else { 1 }" & vbCrLf &
                                     "Set-Content -LiteralPath " & q(codeFile) & " -Value $code" & vbCrLf, New Text.UTF8Encoding(True))
        Try
            Dim psi As New ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{script}""") With {
                .UseShellExecute = True, .Verb = "runas", .WindowStyle = ProcessWindowStyle.Hidden}
            Dim p As Process
            Try
                p = Process.Start(psi)
            Catch ex As ComponentModel.Win32Exception When ex.NativeErrorCode = 1223
                Return (False, "The user said no at Windows' administrator prompt, so the command did not run.")
            End Try
            Using p
                Dim waitTask = p.WaitForExitAsync(ct)
                If Await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 5, 3600)), ct)).ConfigureAwait(False) IsNot waitTask Then
                    Return (True, $"Still running as administrator after {waitSeconds} s (it goes on; its output will be in {outFile}).")
                End If
            End Using
            Dim output = If(IO.File.Exists(outFile), IO.File.ReadAllText(outFile).TrimEnd(), "")
            Dim code = If(IO.File.Exists(codeFile), IO.File.ReadAllText(codeFile).Trim(), "?")
            Return (code = "0", Clip(If(output.Length = 0, "(no output)", output)) & vbLf & $"(ran as administrator; exit code {code})")
        Finally
            Try
                If IO.File.Exists(script) Then IO.File.Delete(script)
            Catch ex As IO.IOException
            End Try
        End Try
    End Function

    Private Async Function RunCommandAsync(command As String, cwd As String, waitSeconds As Integer, ct As CancellationToken, Optional chatId As String = Nothing) As Task(Of (Boolean, String))
        If String.IsNullOrWhiteSpace(command) Then Return (False, "command is required.")
        Dim wait = Math.Clamp(waitSeconds, 5, 3600)
        ' It starts where the chat's last cd left it, and says where it ended (the folder line is taken out of its output).
        Dim folder = CommandFolder(chatId, cwd)
        Dim job = StartJob(WithCwdMarker(command), folder, command)
        Dim how As String
        Try
            how = Await WaitForJobAsync(job, TimeSpan.FromSeconds(wait), ct).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            ' The answer was stopped: so is the command.
            KillCommand(job.Id)
            Throw
        End Try
        Dim split = SplitCwd(TakeRawOutput(job))
        Dim moved = If(how = "running", Nothing, KeepCwd(chatId, folder, split.Folder))
        Dim text = Clip(split.Text.TrimEnd())
        If text.Length = 0 Then text = "(no output)"
        If moved IsNot Nothing Then text &= vbLf & moved
        Select Case how
            Case "finished"
                ForgetJob(job)
                Return (job.ExitCode = 0, text & vbLf & $"(exit code {job.ExitCode})")
            Case "shell-ended"
                Return (job.ExitCode = 0, text & vbLf & $"(exit code {job.ExitCode}; something this command started is still running and printing: it goes on in the background " &
                                                       $"as {job.Id}. See what it prints with command_output {{""id"":""{job.Id}""}}; stop it with kill_command.)")
            Case Else
                Return (True, $"Still running after {wait} s, so it goes on in the background as {job.Id} (it was not stopped)." & vbLf & "Output so far:" & vbLf & text & vbLf &
                              $"Check on it with command_output {{""id"":""{job.Id}""}} (add wait_seconds to wait for it to finish), or stop it with kill_command.")
        End Select
    End Function

    ''' <summary>The user's shell running one command line in a folder (output and errors redirected).</summary>
    Private Function ShellStartInfo(command As String, dir As String) As ProcessStartInfo
        Dim psi As New ProcessStartInfo With {.UseShellExecute = False, .CreateNoWindow = True, .RedirectStandardOutput = True, .RedirectStandardError = True,
                                              .WorkingDirectory = dir, .StandardOutputEncoding = Encoding.UTF8, .StandardErrorEncoding = Encoding.UTF8}
        If Not OperatingSystem.IsWindows() Then
            ' bash (or sh) -c: the command line as the user would type it in a terminal.
            psi.FileName = If(_settings.Shell = "sh" OrElse PlatformUtil.Which("bash") Is Nothing, "/bin/sh", PlatformUtil.Which("bash"))
            psi.ArgumentList.Add("-c")
            psi.ArgumentList.Add(command)
        ElseIf _settings.Shell = "cmd" Then
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe")
            psi.ArgumentList.Add("/d")
            psi.ArgumentList.Add("/s")
            psi.ArgumentList.Add("/c")
            psi.ArgumentList.Add(command)
        Else
            ' PowerShell -Command ends with 1 for any failure: the command's own exit code is kept (a hook's 2 blocks, a test run's
            ' code is reported).
            psi.FileName = "powershell.exe"
            For Each a In {"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " & command & vbLf & PowerShellExitCode}
                psi.ArgumentList.Add(a)
            Next
        End If
        If OperatingSystem.IsWindows() Then psi.Environment("PATH") = CurrentPath(psi.Environment("PATH"))
        Return psi
    End Function

    ''' <summary>The last line of every PowerShell command: it ends with the exit code of its last program (or 1 when a cmdlet failed).</summary>
    Private Const PowerShellExitCode As String = "$__agentic_ok = $?; if ($null -ne $global:LASTEXITCODE -and $global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE } elseif (-not $__agentic_ok) { exit 1 }"

    Private Shared _pathCache As (At As DateTime, Value As String)

    ''' <summary>
    ''' Windows: the PATH as it is now (machine, then user, from the registry), then any other folders this app started with. The
    ''' app keeps the PATH it started with, so a tool installed since (node, git, dotnet) was "not recognized" until a restart.
    ''' Read again at most once a minute.
    ''' </summary>
    Private Shared Function CurrentPath(startPath As String) As String
        Dim cached = _pathCache
        If cached.Value IsNot Nothing AndAlso DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1) Then Return cached.Value
        Dim parts As New List(Of String)
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each source In {Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine), Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User), startPath}
            For Each p In If(source, "").Split(";"c)
                Dim dir = Environment.ExpandEnvironmentVariables(p.Trim())
                If dir.Length > 0 AndAlso seen.Add(dir) Then parts.Add(dir)
            Next
        Next
        Dim value = String.Join(";", parts)
        _pathCache = (DateTime.UtcNow, value)
        Return value
    End Function

    ' ------------------------------------------------------------------ Agentic API

    Private Async Function CallAgenticAsync(method As String, path As String, args As JsonElement, ct As CancellationToken) As Task(Of (Boolean, String))
        If Not path.StartsWith("/api/") AndAlso Not path.StartsWith("/v1/") Then Return (False, "path must start with /api/ or /v1/.")
        If path.StartsWith("/api/local/") Then Return (False, "Desktop endpoints are not available here.")
        Using req As New HttpRequestMessage(New HttpMethod(method.ToUpperInvariant()), _settings.NormalizedBaseUrl() & path)
            If _settings.HasKey() Then req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & _settings.ApiKey)
            Dim body As JsonElement
            If args.TryGetProperty("body", body) AndAlso body.ValueKind <> JsonValueKind.Null Then req.Content = New StringContent(body.GetRawText(), Encoding.UTF8, "application/json")
            Using res = Await Http.SendAsync(req, ct).ConfigureAwait(False)
                Dim text = Await res.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Return (res.IsSuccessStatusCode, $"{CInt(res.StatusCode)} {res.ReasonPhrase}" & vbLf & Clip(text))
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ helpers

    Private Shared Function Clip(text As String) As String
        Return If(text.Length > MaxOutput, text.Substring(0, MaxOutput \ 2) & vbLf & "… (output shortened) …" & vbLf & text.Substring(text.Length - MaxOutput \ 2), text)
    End Function

    Friend Shared Function SizeText(bytes As Long) As String
        Return If(bytes < 1024, bytes & " B", If(bytes < 1048576, (bytes / 1024).ToString("0.#") & " KB", (bytes / 1048576).ToString("0.#") & " MB"))
    End Function

    Friend Shared Function Str(e As JsonElement, name As String, fallback As String) As String
        Dim el As JsonElement
        If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, el) Then Return If(el.ValueKind = JsonValueKind.String, el.GetString(), el.GetRawText())
        Return fallback
    End Function

    Friend Shared Function Int(e As JsonElement, name As String, fallback As Integer) As Integer
        Dim el As JsonElement
        Dim v As Integer
        If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Number AndAlso el.TryGetInt32(v) Then Return v
        Return fallback
    End Function

    Friend Shared Function Bool(e As JsonElement, name As String) As Boolean
        Dim el As JsonElement
        Return e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.True
    End Function

End Class
