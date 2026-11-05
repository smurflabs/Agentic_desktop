Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading

''' <summary>A way to run the project: a dev server, the app, its tests, or the built-in server for a static site.</summary>
Public Class RunConfig
    Public Property Id As String
    Public Property Name As String
    ''' <summary>The command line (in the user's shell); Nothing for the built-in static server.</summary>
    Public Property Command As String
    Public Property Cwd As String
    ''' <summary>web (serves a page) | static (the built-in server) | app | test</summary>
    Public Property Kind As String
    Public Property Port As Integer
    Public Property Url As String
    ''' <summary>Where it came from: .agentic/launch.json, .claude/launch.json or "detected".</summary>
    Public Property Source As String
    ''' <summary>The project (workspace folder) it belongs to: its id and name.</summary>
    Public Property Project As String
    Public Property ProjectName As String
End Class

''' <summary>A project: a workspace folder, and what of it can be shared as a website (a folder with index.html), if anything.</summary>
Public Class RunProject
    Public Property Id As String
    Public Property Name As String
    Public Property Path As String
    ''' <summary>The folder Share uploads, relative to the project ("." = the project itself); Nothing = nothing to share yet.</summary>
    Public Property Share As String
    ''' <summary>The project's id on the server (sharing it again updates the same link).</summary>
    Public Property Key As String
End Class

''' <summary>
''' Running the user's project (the Run button in Chat, and the model's preview tool), like Claude Desktop's preview: how to run
''' it comes from .agentic/launch.json or Claude Code's .claude/launch.json, else from the project's files - package.json
''' scripts, a static site (served by a small built-in server: ES modules do not load from file://), Python, .NET, Go, Rust, a
''' Makefile. A web app's address comes from its output (or its port) and opens in the preview pane next to the chat.
''' </summary>
Partial Public Class LocalTools

    Private NotInheritable Class RunState
        Public Config As RunConfig
        Public Proc As Process
        Public ReadOnly Output As New StringBuilder()
        Public Url As String
        Public Started As DateTime
        Public ExitCode As Integer?
        Public Site As StaticSite
        Public Stopped As Boolean
        ''' <summary>Output streams that have ended (2: all its output is in).</summary>
        Public Eofs As Integer
    End Class

    Private ReadOnly _runs As New ConcurrentDictionary(Of String, RunState)(StringComparer.OrdinalIgnoreCase)
    Private Shared ReadOnly UrlPattern As New Regex("https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\]|[a-z0-9-]+\.local)(?::\d{2,5})?(?:/[^\s""'<>)\]]*)?", RegexOptions.IgnoreCase)
    Private Shared ReadOnly AnsiCodes As New Regex("\x1B\[[0-9;?]*[ -/]*[@-~]")

    ''' <summary>Opens a page in the preview pane next to the chat (set by the window): the address and a title.</summary>
    Public Property ShowPreview As Action(Of String, String)

    ''' <summary>The project folder: the first workspace folder.</summary>
    Public Function ProjectRoot() As String
        Return _settings.EffectiveWorkspaces()(0)
    End Function

    ''' <summary>The projects: the workspace folders (at most six).</summary>
    Public Function RunProjects() As List(Of RunProject)
        Return _settings.EffectiveWorkspaces().Take(6).Select(Function(root) New RunProject With {
            .Id = ProjectId(root), .Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), .Path = root, .Share = ShareFolder(root),
            .Key = _settings.DeviceId & ":" & ProjectId(root)}).ToList()
    End Function

    Private Shared Function ProjectId(root As String) As String
        Dim bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant()))
        Return "p" & Convert.ToHexString(bytes).Substring(0, 10).ToLowerInvariant()
    End Function

    ''' <summary>
    ''' What Share uploads: the build (dist, build, out, _site, public, www) when it has index.html, else the project itself when it
    ''' is a static site (index.html, and no package.json whose build makes the real site).
    ''' </summary>
    Public Shared Function ShareFolder(root As String) As String
        For Each d In {"dist", "build", "out", "_site", "public", "www"}
            If IO.File.Exists(Path.Combine(root, d, "index.html")) Then Return d
        Next
        If Not IO.File.Exists(Path.Combine(root, "index.html")) Then Return Nothing
        If IO.File.Exists(Path.Combine(root, "package.json")) AndAlso SafeHead(Path.Combine(root, "package.json")).Contains("""build""") Then Return Nothing
        Return "."
    End Function

    ''' <summary>The ways to run the projects: each one's launch.json, else what its files show.</summary>
    Public Function RunConfigs() As List(Of RunConfig)
        Dim all As New List(Of RunConfig)
        Dim projects = RunProjects()
        For Each p In projects
            Dim list = LaunchConfigs(p.Path)
            If list.Count = 0 Then list = DetectRunConfigs(p.Path)
            For Each c In list.Take(12)
                c.Project = p.Id
                c.ProjectName = p.Name
                all.Add(c)
            Next
        Next
        ' Stable, unique ids from the names (the first project's as they are; the others' with their folder's name first).
        Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each c In all
            Dim name = If(c.Project = projects(0).Id, c.Name, c.ProjectName & " " & c.Name)
            Dim id = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim("-"c)
            If id.Length = 0 Then id = "run"
            Dim unique = id, n = 2
            While Not seen.Add(unique)
                unique = id & "-" & n
                n += 1
            End While
            c.Id = unique
        Next
        Return all.Take(40).ToList()
    End Function

    ''' <summary>
    ''' Share: uploads the project's website (ShareFolder: its files, without node_modules, .git and hidden files) to the Agentic
    ''' server, which gives it a public link; sharing the same project again puts the new files under the same link.
    ''' </summary>
    Public Async Function ShareProjectAsync(projectId As String, ct As CancellationToken) As Task(Of JsonNode)
        Dim p = RunProjects().FirstOrDefault(Function(x) String.Equals(x.Id, projectId, StringComparison.OrdinalIgnoreCase) OrElse
                                                         String.Equals(x.Name, projectId, StringComparison.OrdinalIgnoreCase))
        If p Is Nothing Then Throw New KeyNotFoundException("No such project.")
        If p.Share Is Nothing Then Throw New InvalidOperationException($"{p.Name} has no website to share yet: it needs index.html, or a build (npm run build makes dist).")
        Dim folder = Path.GetFullPath(Path.Combine(p.Path, p.Share))
        Dim zip = ZipSite(folder, 25L * 1024 * 1024)
        Dim query = $"?title={Uri.EscapeDataString(p.Name)}&source={Uri.EscapeDataString(p.Name)}&key={Uri.EscapeDataString(p.Key)}"
        Using req As New Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Post, _settings.NormalizedBaseUrl() & "/api/chat/artifacts/share-site" & query)
            If _settings.HasKey() Then req.Headers.TryAddWithoutValidation("Authorization", "Bearer " & _settings.ApiKey)
            req.Content = New Net.Http.ByteArrayContent(zip)
            req.Content.Headers.ContentType = New Net.Http.Headers.MediaTypeHeaderValue("application/zip")
            Using res = Await Http.SendAsync(req, ct).ConfigureAwait(False)
                Dim text = Await res.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Dim node As JsonNode = Nothing
                Try
                    node = JsonNode.Parse(text)
                Catch ex As JsonException
                End Try
                If Not res.IsSuccessStatusCode Then
                    Dim msg = node?("error")?("message")?.ToString()
                    Throw New InvalidOperationException(If(msg, $"The server answered {CInt(res.StatusCode)} {res.ReasonPhrase}."))
                End If
                If node Is Nothing Then Throw New InvalidOperationException("The server's answer could not be read.")
                Return node
            End Using
        End Using
    End Function

    ''' <summary>A folder as a zip (no node_modules, .git or hidden files and folders); refused when it is larger than maxBytes.</summary>
    Public Shared Function ZipSite(folder As String, maxBytes As Long) As Byte()
        Dim skip = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"node_modules", "__pycache__", "bin", "obj"}
        Dim total As Long = 0, count = 0
        Using ms As New MemoryStream()
            Using archive As New IO.Compression.ZipArchive(ms, IO.Compression.ZipArchiveMode.Create, leaveOpen:=True)
                Dim walk As Action(Of String) = Nothing
                walk = Sub(dir As String)
                           For Each f In Directory.EnumerateFiles(dir)
                               If Path.GetFileName(f).StartsWith("."c) Then Continue For
                               total += New FileInfo(f).Length
                               count += 1
                               If total > maxBytes Then Throw New InvalidOperationException($"The website is larger than {maxBytes \ (1024 * 1024)} MB.")
                               If count > 3000 Then Throw New InvalidOperationException("The website has more than 3000 files.")
                               Dim rel = Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, "/"c)
                               archive.CreateEntryFromFile(f, rel, IO.Compression.CompressionLevel.Fastest)
                           Next
                           For Each d In Directory.EnumerateDirectories(dir)
                               Dim name = Path.GetFileName(d)
                               If Not name.StartsWith("."c) AndAlso Not skip.Contains(name) Then walk(d)
                           Next
                       End Sub
                walk(folder)
            End Using
            Return ms.ToArray()
        End Using
    End Function

    ''' <summary>.agentic/launch.json, else .claude/launch.json: {configurations: [{name, command | runtimeExecutable + runtimeArgs, cwd, port, url, kind}]}.</summary>
    Public Shared Function LaunchConfigs(root As String) As List(Of RunConfig)
        For Each rel In {Path.Combine(".agentic", "launch.json"), Path.Combine(".claude", "launch.json")}
            Dim file = Path.Combine(root, rel)
            If Not IO.File.Exists(file) Then Continue For
            Dim list As New List(Of RunConfig)
            Try
                Using doc = JsonDocument.Parse(IO.File.ReadAllText(file), New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Dim arr As JsonElement
                    If Not doc.RootElement.TryGetProperty("configurations", arr) OrElse arr.ValueKind <> JsonValueKind.Array Then Continue For
                    For Each e In arr.EnumerateArray()
                        If e.ValueKind <> JsonValueKind.Object Then Continue For
                        Dim command = Str(e, "command", Nothing)
                        Dim exe = Str(e, "runtimeExecutable", Nothing)
                        If String.IsNullOrWhiteSpace(command) AndAlso Not String.IsNullOrWhiteSpace(exe) Then
                            Dim args As New List(Of String) From {Quote(exe)}
                            Dim ra As JsonElement
                            If e.TryGetProperty("runtimeArgs", ra) AndAlso ra.ValueKind = JsonValueKind.Array Then
                                args.AddRange(ra.EnumerateArray().Where(Function(a) a.ValueKind = JsonValueKind.String).Select(Function(a) Quote(a.GetString())))
                            End If
                            command = String.Join(" ", args)
                        End If
                        Dim port = Int(e, "port", 0)
                        Dim url = Str(e, "url", Nothing)
                        Dim name = Str(e, "name", If(command, "Run"))
                        Dim kind = Str(e, "kind", Nothing)
                        If String.IsNullOrWhiteSpace(kind) Then kind = If(Regex.IsMatch(name, "\btests?\b", RegexOptions.IgnoreCase), "test", If(port > 0 OrElse url IsNot Nothing, "web", "app"))
                        If String.IsNullOrWhiteSpace(command) AndAlso kind <> "static" Then Continue For
                        Dim cwd = Str(e, "cwd", ".")
                        list.Add(New RunConfig With {.Name = name, .Command = command, .Kind = kind.ToLowerInvariant(), .Port = port, .Url = url,
                                                     .Cwd = Path.GetFullPath(Path.Combine(root, If(String.IsNullOrWhiteSpace(cwd), ".", cwd))), .Source = rel.Replace("\"c, "/"c)})
                    Next
                End Using
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Continue For
            End Try
            If list.Count > 0 Then Return list
        Next
        Return New List(Of RunConfig)
    End Function

    Private Shared Function Quote(s As String) As String
        Return If(s Is Nothing, "", If(s.Contains(" "c) AndAlso Not s.StartsWith(""""), """" & s & """", s))
    End Function

    ''' <summary>What the project's files show: package.json scripts, a static site, Python, .NET, Go, Rust, a Makefile.</summary>
    Public Shared Function DetectRunConfigs(root As String) As List(Of RunConfig)
        Dim list As New List(Of RunConfig)
        Dim add = Sub(name As String, command As String, kind As String, port As Integer)
                      If list.Any(Function(c) String.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase) AndAlso c.Kind = kind) Then Return
                      list.Add(New RunConfig With {.Name = name, .Command = command, .Kind = kind, .Port = port, .Cwd = root, .Source = "detected"})
                  End Sub
        Dim has = Function(rel As String) IO.File.Exists(Path.Combine(root, rel))
        ' Node: the package.json scripts, run with the package manager the lock file names.
        If has("package.json") Then
            Dim pm = If(has("pnpm-lock.yaml"), "pnpm", If(has("yarn.lock"), "yarn", If(has("bun.lockb") OrElse has("bun.lock"), "bun", "npm")))
            Try
                Using doc = JsonDocument.Parse(IO.File.ReadAllText(Path.Combine(root, "package.json")), New JsonDocumentOptions With {.AllowTrailingCommas = True})
                    Dim scripts As JsonElement
                    If doc.RootElement.TryGetProperty("scripts", scripts) AndAlso scripts.ValueKind = JsonValueKind.Object Then
                        Dim names = scripts.EnumerateObject().Select(Function(p) p.Name).ToList()
                        For Each s In {("dev", "Dev server"), ("start", "Start"), ("serve", "Serve"), ("preview", "Preview the build")}
                            If names.Contains(s.Item1) Then add(s.Item2, $"{pm} run {s.Item1}", "web", 0)
                        Next
                        If names.Contains("build") Then add("Build", $"{pm} run build", "app", 0)
                        If names.Contains("test") Then add("Tests", $"{pm} run test", "test", 0)
                    End If
                End Using
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
            End Try
        End If
        ' A static site (a page, a game): the built-in server serves the folder.
        If has("index.html") AndAlso Not list.Any(Function(c) c.Kind = "web") Then
            list.Add(New RunConfig With {.Name = "Static site", .Kind = "static", .Cwd = root, .Source = "detected"})
        End If
        ' Python
        If has("manage.py") Then add("Django server", "python manage.py runserver", "web", 8000)
        For Each f In {"app.py", "main.py", "server.py"}
            If Not has(f) Then Continue For
            Dim code = SafeHead(Path.Combine(root, f))
            Dim stem = Path.GetFileNameWithoutExtension(f)
            If Regex.IsMatch(code, "\bfastapi\b", RegexOptions.IgnoreCase) Then
                add("Web app", $"python -m uvicorn {stem}:app --reload", "web", 8000)
            ElseIf Regex.IsMatch(code, "\bflask\b", RegexOptions.IgnoreCase) Then
                add("Web app", $"python {f}", "web", 5000)
            ElseIf Not list.Any(Function(c) c.Command IsNot Nothing AndAlso c.Command.StartsWith("python ")) Then
                add("Run " & f, $"python {f}", "app", 0)
            End If
        Next
        If has("pytest.ini") OrElse has("conftest.py") OrElse (has("pyproject.toml") AndAlso SafeHead(Path.Combine(root, "pyproject.toml")).Contains("pytest")) OrElse
           (Directory.Exists(Path.Combine(root, "tests")) AndAlso Directory.EnumerateFiles(Path.Combine(root, "tests"), "test_*.py").Any()) Then
            add("Tests", "python -m pytest", "test", 0)
        End If
        ' .NET: a web project, an app, test projects.
        Dim projects = SafeFiles(root, {"*.csproj", "*.vbproj", "*.fsproj"}, 3)
        Dim testAdded = False
        For Each p In projects
            Dim xml = SafeHead(p)
            Dim rel = Path.GetRelativePath(root, p)
            Dim nice = Path.GetFileNameWithoutExtension(p)
            If Regex.IsMatch(xml, "Microsoft\.NET\.Test\.Sdk|xunit|nunit|MSTest", RegexOptions.IgnoreCase) Then
                If Not testAdded Then add("Tests", "dotnet test", "test", 0)
                testAdded = True
            ElseIf xml.Contains("Microsoft.NET.Sdk.Web") Then
                add($"Web app ({nice})", $"dotnet run --project ""{rel}""", "web", 0)
            ElseIf Regex.IsMatch(xml, "<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase) Then
                add($"Run {nice}", $"dotnet run --project ""{rel}""", "app", 0)
            End If
        Next
        ' Go, Rust, a Makefile
        If has("go.mod") Then
            add("Run", "go run .", "app", 0)
            add("Tests", "go test ./...", "test", 0)
        End If
        If has("Cargo.toml") Then
            add("Run", "cargo run", "app", 0)
            add("Tests", "cargo test", "test", 0)
        End If
        If has("Makefile") Then
            For Each m As Match In Regex.Matches(SafeHead(Path.Combine(root, "Makefile")), "^(run|dev|start|serve|test):", RegexOptions.Multiline)
                Dim t = m.Groups(1).Value
                add("make " & t, "make " & t, If(t = "test", "test", If(t = "run", "app", "web")), 0)
            Next
        End If
        Return list
    End Function

    Private Shared Function SafeHead(file As String) As String
        Try
            Using r As New StreamReader(file)
                Dim buf(200000) As Char
                Dim n = r.ReadBlock(buf, 0, buf.Length)
                Return New String(buf, 0, n)
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return ""
        End Try
    End Function

    ''' <summary>Files matching the patterns, a few folders deep, skipping build, dependency and hidden folders (and AppData when the workspace is the home folder).</summary>
    Private Shared Function SafeFiles(root As String, patterns As String(), depth As Integer) As List(Of String)
        Dim found As New List(Of String)
        Dim skip = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"node_modules", "bin", "obj", "dist", "build", "target", "venv", "packages", "AppData", "Library"}
        Dim walk As Action(Of String, Integer) = Nothing
        walk = Sub(dir As String, level As Integer)
                   If found.Count > 40 Then Return
                   Try
                       For Each p In patterns
                           found.AddRange(Directory.EnumerateFiles(dir, p))
                       Next
                       If level >= depth Then Return
                       For Each d In Directory.EnumerateDirectories(dir)
                           Dim name = Path.GetFileName(d)
                           If Not name.StartsWith("."c) AndAlso Not skip.Contains(name) Then walk(d, level + 1)
                       Next
                   Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                   End Try
               End Sub
        walk(root, 1)
        Return found
    End Function

    ''' <summary>Starts a configuration (stopping it first when it runs); its output is kept and a web app's address found in it.</summary>
    Public Function StartRun(id As String) As RunConfig
        Dim cfg = RunConfigs().FirstOrDefault(Function(c) String.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) OrElse
                                                         String.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase))
        If cfg Is Nothing Then Throw New KeyNotFoundException($"No run configuration ""{id}"". Known: {String.Join(", ", RunConfigs().Select(Function(c) c.Name))}.")
        StopRun(cfg.Id)
        Dim st As New RunState With {.Config = cfg, .Started = DateTime.UtcNow}
        If cfg.Kind = "static" Then
            st.Site = New StaticSite(cfg.Cwd)
            st.Url = st.Site.Start()
            st.Output.AppendLine($"Serving {cfg.Cwd} at {st.Url}")
        Else
            ' PowerShell -Command ends with 1 for any failure: the run keeps the command's own exit code (tests report theirs).
            Dim command = cfg.Command
            If OperatingSystem.IsWindows() AndAlso _settings.Shell <> "cmd" Then command &= vbLf & "$ok = $?; if (-not $ok) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } }"
            Dim psi = ShellStartInfo(command, cfg.Cwd)
            ' Dev servers must not open a browser of their own, and plain text reads better than colour codes.
            psi.Environment("BROWSER") = "none"
            psi.Environment("FORCE_COLOR") = "0"
            psi.Environment("NO_COLOR") = "1"
            If cfg.Port > 0 Then psi.Environment("PORT") = cfg.Port.ToString(Globalization.CultureInfo.InvariantCulture)
            st.Proc = New Process With {.StartInfo = psi, .EnableRaisingEvents = True}
            Dim add As DataReceivedEventHandler =
                Sub(s, e)
                    If e.Data Is Nothing Then
                        Interlocked.Increment(st.Eofs)
                        Return
                    End If
                    Dim line = AnsiCodes.Replace(e.Data, "")
                    SyncLock st.Output
                        st.Output.AppendLine(line)
                        If st.Output.Length > 2000000 Then st.Output.Remove(0, st.Output.Length - 1000000)
                    End SyncLock
                    If st.Url Is Nothing AndAlso (cfg.Kind = "web" OrElse cfg.Kind = "app") Then
                        Dim m = UrlPattern.Match(line)
                        If m.Success Then st.Url = Regex.Replace(m.Value.TrimEnd("."c, ","c), "//(0\.0\.0\.0|\[::\])", "//localhost")
                    End If
                End Sub
            AddHandler st.Proc.OutputDataReceived, add
            AddHandler st.Proc.ErrorDataReceived, add
            AddHandler st.Proc.Exited, Sub(s, e)
                                           ' Its last lines may still be on their way: wait (briefly - a child may keep the pipes open) for both streams to end.
                                           Dim waited = 0
                                           While Volatile.Read(st.Eofs) < 2 AndAlso waited < 2000
                                               Thread.Sleep(50)
                                               waited += 50
                                           End While
                                           Try
                                               st.ExitCode = st.Proc.ExitCode
                                           Catch ex As InvalidOperationException
                                               st.ExitCode = -1
                                           End Try
                                       End Sub
            st.Output.AppendLine("> " & cfg.Command)
            st.Proc.Start()
            ChildJob.Add(st.Proc)
            st.Proc.BeginOutputReadLine()
            st.Proc.BeginErrorReadLine()
            ' A configured address or port: it is the page once the port answers.
            If cfg.Kind = "web" AndAlso (cfg.Url IsNot Nothing OrElse cfg.Port > 0) Then
                Dim unused = Task.Run(Function() WaitForPortAsync(st))
            End If
        End If
        _runs(cfg.Id) = st
        Return cfg
    End Function

    Private Shared Async Function WaitForPortAsync(st As RunState) As Task
        Dim url = If(st.Config.Url, $"http://localhost:{st.Config.Port}/")
        Dim u As Uri = Nothing
        If Not Uri.TryCreate(url, UriKind.Absolute, u) Then Return
        For i = 1 To 240
            If st.Url IsNot Nothing OrElse st.ExitCode.HasValue OrElse st.Stopped Then Return
            Try
                Using c As New TcpClient()
                    Await c.ConnectAsync(If(u.Host = "localhost", "127.0.0.1", u.Host), u.Port).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(False)
                    If c.Connected Then
                        st.Url = u.ToString()
                        Return
                    End If
                End Using
            Catch ex As Exception When TypeOf ex Is SocketException OrElse TypeOf ex Is TimeoutException OrElse TypeOf ex Is ObjectDisposedException
            End Try
            Await Task.Delay(500).ConfigureAwait(False)
        Next
    End Function

    ''' <summary>Stops a run; False when it was not running.</summary>
    Public Function StopRun(id As String) As Boolean
        Dim st As RunState = Nothing
        If Not _runs.TryGetValue(id, st) OrElse Not Running(st) Then Return False
        st.Stopped = True
        st.Site?.Dispose()
        Try
            If st.Proc IsNot Nothing AndAlso Not st.Proc.HasExited Then st.Proc.Kill(True)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
        End Try
        If Not st.ExitCode.HasValue Then st.ExitCode = If(st.Site IsNot Nothing, 0, -1)
        SyncLock st.Output
            st.Output.AppendLine("(stopped)")
        End SyncLock
        Return True
    End Function

    ''' <summary>Stops everything this app started (the app is closing).</summary>
    Public Sub StopRuns()
        For Each id In _runs.Keys.ToList()
            StopRun(id)
        Next
    End Sub

    Private Shared Function Running(st As RunState) As Boolean
        Return Not st.Stopped AndAlso (st.Site IsNot Nothing OrElse (st.Proc IsNot Nothing AndAlso Not st.ExitCode.HasValue))
    End Function

    ''' <summary>What runs (and how the last runs ended), for the Run button.</summary>
    Public Function RunStatus() As JsonArray
        Dim list As New JsonArray()
        For Each s In _runs.Values.OrderBy(Function(x) x.Started)
            Dim live = Running(s)
            list.Add(New JsonObject From {{"id", s.Config.Id}, {"name", s.Config.Name}, {"kind", s.Config.Kind}, {"command", s.Config.Command}, {"running", live},
                                          {"url", s.Url}, {"exitCode", If(live, Nothing, s.ExitCode)}, {"startedUtc", s.Started}})
        Next
        Return list
    End Function

    ''' <summary>A run's output from a position (what the log shows as it comes): {text, end, running, exitCode, url}.</summary>
    Public Function RunLog(id As String, from As Integer) As JsonObject
        Dim st As RunState = Nothing
        If Not _runs.TryGetValue(id, st) Then Return New JsonObject From {{"text", ""}, {"end", 0}, {"running", False}, {"exitCode", Nothing}, {"url", Nothing}}
        Dim text As String
        Dim endPos As Integer
        SyncLock st.Output
            Dim start = Math.Max(0, Math.Min(from, st.Output.Length))
            If st.Output.Length - start > 200000 Then start = st.Output.Length - 200000
            text = st.Output.ToString(start, st.Output.Length - start)
            endPos = st.Output.Length
        End SyncLock
        Dim live = Running(st)
        Return New JsonObject From {{"text", text}, {"end", endPos}, {"running", live}, {"exitCode", If(live, Nothing, st.ExitCode)}, {"url", st.Url}}
    End Function

    ''' <summary>.agentic/launch.json, created with what was detected when it is not there yet; returns its path.</summary>
    Public Function EnsureLaunchFile() As String
        Dim root = ProjectRoot()
        Dim file = Path.Combine(root, ".agentic", "launch.json")
        If IO.File.Exists(file) Then Return file
        Directory.CreateDirectory(Path.GetDirectoryName(file))
        Dim configs As New JsonArray()
        For Each c In DetectRunConfigs(root).Where(Function(x) x.Command IsNot Nothing)
            Dim o As New JsonObject From {{"name", c.Name}, {"command", c.Command}, {"cwd", "."}, {"kind", c.Kind}}
            If c.Port > 0 Then o("port") = c.Port
            configs.Add(o)
        Next
        Dim doc As New JsonObject From {{"version", "0.0.1"}, {"configurations", configs}}
        IO.File.WriteAllText(file, doc.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}) & vbLf)
        Return file
    End Function

    ' ------------------------------------------------------------------ the model's preview tool

    Private Shared Function RunDefinitions() As List(Of JsonObject)
        Return New List(Of JsonObject) From {
            Def("preview", "Run this project and show it to the user in the preview pane next to the chat (like Claude Desktop's preview); use it after you build or " &
                "change something they can run (a web page, an app, a game) so they see it, and to run its tests. action status lists " &
                "the run configurations (a dev server, the static site, the app, its tests) and what runs; start runs one (name) and opens a web app in the " &
                "preview once it has an address; open shows a page (url) in the preview; stop stops one (name) or all; share uploads a project's " &
                "website (its build or static files: name = the project) and returns a public link anyone can open - only when the user asks to share it. " &
                "The configurations come from .agentic/launch.json (or Claude Code's .claude/launch.json), else from the project's files; when they are " &
                "missing or wrong, write .agentic/launch.json: {""configurations"": [{""name"", ""runtimeExecutable"", ""runtimeArgs"": [], ""cwd"", ""port"", ""kind"": ""web|app|test""}]}.",
                "{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""status"",""start"",""open"",""stop"",""share""]},""name"":{""type"":""string"",""description"":""the configuration (start, stop) or the project (share)""}," &
                """url"":{""type"":""string"",""description"":""the page to show (open)""}},""required"":[""action""]}")}
    End Function

    Private Async Function PreviewToolAsync(args As JsonElement, ct As CancellationToken) As Task(Of (Boolean, String))
        Select Case Str(args, "action", "status").ToLowerInvariant()
            Case "share"
                Dim projects = RunProjects()
                Dim name = Str(args, "name", "")
                Dim p = If(String.IsNullOrWhiteSpace(name), If(projects.FirstOrDefault(Function(x) x.Share IsNot Nothing), projects.FirstOrDefault()),
                           projects.FirstOrDefault(Function(x) x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 OrElse x.Id = name))
                If p Is Nothing Then Return (False, $"No project {name}. Projects: {String.Join(", ", projects.Select(Function(x) x.Name))}.")
                Try
                    Dim r = Await ShareProjectAsync(p.Id, ct).ConfigureAwait(False)
                    Dim link = r?("link")
                    Return (True, $"{If(r?("updated")?.GetValue(Of Boolean)(), "Updated", "Shared")} {p.Name} ({p.Share}): {link?("url")} - anyone with this link can open it.")
                Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is IOException
                    Return (False, ex.Message)
                End Try
            Case "open"
                Dim url = Str(args, "url", "")
                Dim u As Uri = Nothing
                If Not Uri.TryCreate(url, UriKind.Absolute, u) OrElse (u.Scheme <> Uri.UriSchemeHttp AndAlso u.Scheme <> Uri.UriSchemeHttps) Then Return (False, "url must be an http(s) address.")
                If ShowPreview Is Nothing Then Return (False, "There is no preview pane here.")
                ShowPreview.Invoke(u.ToString(), u.Host)
                Return (True, $"Showing {u} in the preview pane.")
            Case "stop"
                Dim name = Str(args, "name", "")
                Dim ids = If(String.IsNullOrWhiteSpace(name), _runs.Keys.ToList(), RunConfigs().Where(Function(c) Matches(c, name)).Select(Function(c) c.Id).ToList())
                Dim stopped = ids.Where(Function(i) StopRun(i)).Count()
                Return (True, If(stopped = 0, "Nothing was running.", $"Stopped {stopped}."))
            Case "start"
                Dim configs = RunConfigs()
                Dim name = Str(args, "name", "")
                Dim cfg = If(String.IsNullOrWhiteSpace(name), configs.FirstOrDefault(Function(c) c.Kind = "web" OrElse c.Kind = "static"), configs.FirstOrDefault(Function(c) Matches(c, name)))
                If cfg Is Nothing Then Return (False, $"No run configuration {If(name, "")}. Known: {If(configs.Count = 0, "none (write .agentic/launch.json)", String.Join(", ", configs.Select(Function(c) $"{c.Name} ({c.Command})")))}.")
                StartRun(cfg.Id)
                Dim st = _runs(cfg.Id)
                ' A web app: wait for its address (a minute at most); tests: wait until they end (ten minutes); an app: its first output.
                Dim deadline = DateTime.UtcNow.AddSeconds(If(cfg.Kind = "test", 600, If(cfg.Kind = "app", 8, 60)))
                While DateTime.UtcNow < deadline AndAlso Not ct.IsCancellationRequested
                    If st.ExitCode.HasValue OrElse ((cfg.Kind = "web" OrElse cfg.Kind = "static") AndAlso st.Url IsNot Nothing) Then Exit While
                    Await Task.Delay(300, ct).ConfigureAwait(False)
                End While
                Dim tail As String
                SyncLock st.Output
                    tail = String.Join(vbLf, st.Output.ToString().Split({vbCrLf, vbLf}, StringSplitOptions.None).TakeLast(40))
                End SyncLock
                If st.Url IsNot Nothing AndAlso ShowPreview IsNot Nothing AndAlso (cfg.Kind = "web" OrElse cfg.Kind = "static") Then ShowPreview.Invoke(st.Url, cfg.Name)
                Dim state = If(st.ExitCode.HasValue, $"It ended with exit code {st.ExitCode}.", If(st.Url IsNot Nothing, $"It runs at {st.Url} and shows in the preview pane.", "It is running."))
                Return (st.ExitCode.GetValueOrDefault() = 0, $"Started {cfg.Name} ({If(cfg.Command, "built-in server")}). {state}" & vbLf & "Last output:" & vbLf & tail)
            Case Else
                Dim configs = RunConfigs()
                Dim sb As New StringBuilder()
                sb.AppendLine(If(configs.Count = 0, "No run configurations: write .agentic/launch.json.", "Run configurations (" & If(configs(0).Source, "detected") & "):"))
                For Each c In configs
                    Dim st As RunState = Nothing
                    Dim live = _runs.TryGetValue(c.Id, st) AndAlso Running(st)
                    sb.AppendLine($"- {c.Name} [{c.Kind}]: {If(c.Command, "built-in server")}{If(live, " - running" & If(st.Url IsNot Nothing, " at " & st.Url, ""), "")}")
                Next
                Return (True, sb.ToString().TrimEnd())
        End Select
    End Function

    Private Shared Function Matches(c As RunConfig, name As String) As Boolean
        Return String.Equals(c.Id, name, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) OrElse
               c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
    End Function

End Class

''' <summary>
''' A small web server for a static site (the Run button's "Static site"): serves one folder on 127.0.0.1 with the right content
''' types and no caching, so a reload shows the latest files. Nothing outside the folder is served.
''' </summary>
Public NotInheritable Class StaticSite
    Implements IDisposable

    Private ReadOnly _root As String
    Private _listener As HttpListener
    Private ReadOnly _cts As New CancellationTokenSource()
    Private Shared ReadOnly Types As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {".html", "text/html; charset=utf-8"}, {".htm", "text/html; charset=utf-8"}, {".js", "text/javascript; charset=utf-8"}, {".mjs", "text/javascript; charset=utf-8"},
        {".css", "text/css; charset=utf-8"}, {".json", "application/json; charset=utf-8"}, {".map", "application/json"}, {".txt", "text/plain; charset=utf-8"},
        {".svg", "image/svg+xml"}, {".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"}, {".webp", "image/webp"},
        {".ico", "image/x-icon"}, {".bmp", "image/bmp"}, {".wav", "audio/wav"}, {".mp3", "audio/mpeg"}, {".ogg", "audio/ogg"}, {".m4a", "audio/mp4"},
        {".mp4", "video/mp4"}, {".webm", "video/webm"}, {".woff", "font/woff"}, {".woff2", "font/woff2"}, {".ttf", "font/ttf"}, {".otf", "font/otf"},
        {".wasm", "application/wasm"}, {".xml", "application/xml"}, {".pdf", "application/pdf"}}

    Public Sub New(root As String)
        _root = Path.GetFullPath(root)
    End Sub

    ''' <summary>Listens on a free port; returns the site's address.</summary>
    Public Function Start() As String
        Dim port As Integer
        Using s As New Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            s.Bind(New IPEndPoint(IPAddress.Loopback, 0))
            port = DirectCast(s.LocalEndPoint, IPEndPoint).Port
        End Using
        _listener = New HttpListener()
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        _listener.Start()
        Dim unused = Task.Run(AddressOf ServeAsync)
        Return $"http://127.0.0.1:{port}/"
    End Function

    Private Async Function ServeAsync() As Task
        While Not _cts.IsCancellationRequested
            Dim ctx As HttpListenerContext
            Try
                ctx = Await _listener.GetContextAsync().ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                Return
            End Try
            Dim unused = Task.Run(Function() HandleAsync(ctx))
        End While
    End Function

    Private Async Function HandleAsync(ctx As HttpListenerContext) As Task
        Dim res = ctx.Response
        Try
            Dim rel = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).TrimStart("/"c).Replace("/"c, Path.DirectorySeparatorChar)
            Dim full = Path.GetFullPath(Path.Combine(_root, rel))
            Dim inside = full.Equals(_root, StringComparison.OrdinalIgnoreCase) OrElse full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            If inside AndAlso Directory.Exists(full) Then full = Path.Combine(full, "index.html")
            If Not inside OrElse Not File.Exists(full) Then
                res.StatusCode = 404
                Dim msg = Encoding.UTF8.GetBytes("Not found")
                Await res.OutputStream.WriteAsync(msg).ConfigureAwait(False)
                Return
            End If
            Dim type As String = Nothing
            res.ContentType = If(Types.TryGetValue(Path.GetExtension(full), type), type, "application/octet-stream")
            res.Headers("Cache-Control") = "no-store"
            Dim bytes = Await File.ReadAllBytesAsync(full).ConfigureAwait(False)
            res.ContentLength64 = bytes.Length
            If ctx.Request.HttpMethod <> "HEAD" Then Await res.OutputStream.WriteAsync(bytes).ConfigureAwait(False)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            Try
                res.StatusCode = 500
            Catch inner As InvalidOperationException
            End Try
        Finally
            Try
                res.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
            End Try
        End Try
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        Try
            _listener?.Stop()
            _listener?.Close()
        Catch ex As ObjectDisposedException
        End Try
    End Sub
End Class
