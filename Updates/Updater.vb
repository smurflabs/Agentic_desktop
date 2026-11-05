Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading

''' <summary>What desktop.js shows about updates (GET /api/local/update).</summary>
Public Class UpdateStatus
    ''' <summary>off | dev | idle | checking | current | none | available | downloading | ready | error</summary>
    Public Property State As String = "idle"
    Public Property CurrentVersion As String
    Public Property LatestVersion As String
    Public Property Notes As String
    Public Property Reason As String
    Public Property [Error] As String
    Public Property Progress As Integer
    Public Property LastCheckUtc As DateTime?
    ''' <summary>Files of www\desktop replaced since the app started (the page reloads to use them).</summary>
    Public Property FilesUpdated As Integer
    Public Property AutoUpdate As Boolean
End Class

''' <summary>
''' Keeps Agentic Desktop up to date from the Agentic server (GET /api/updates/desktop; on Linux /api/updates/desktop-linux-x64,
''' the Linux build's own channel):
'''  - www\desktop: files whose SHA-256 differs from the server's are downloaded and replaced; files the package no
'''    longer has are removed. Done at start before the window loads, so the page gets the new desktop layer.
'''  - AgenticClient.exe (AgenticClient on Linux): when the server's build is newer (or the same version rebuilt), it is
'''    downloaded, checked against the server's SHA-256 and put in place of the running exe (a running exe can be renamed:
'''    it becomes AgenticClient.exe.old-…; on Linux the new file gets the old one's file mode). "Restart" starts the new exe;
'''    it syncs www\desktop for itself.
''' Only published single-file builds update; a development build (bin\Debug) never changes itself.
''' </summary>
Public Class Updater

    ''' <summary>This build's update channel on the server ("desktop", "desktop-linux-x64").</summary>
    Public Shared ReadOnly App As String = PlatformUtil.UpdateApp("desktop")
    Private ReadOnly _settings As AppSettings
    Private ReadOnly _exe As String = Environment.ProcessPath
    Private ReadOnly _root As String = AppContext.BaseDirectory
    Private ReadOnly _gate As New SemaphoreSlim(1, 1)
    Private ReadOnly _status As New UpdateStatus()
    Private _selfSha As String
    Private _timer As Timer
    Private _interval As TimeSpan = TimeSpan.FromHours(6)

    Public Sub New(settings As AppSettings)
        _settings = settings
        _status.CurrentVersion = CurrentVersion()
        _status.AutoUpdate = settings.AutoUpdate
        _status.State = If(Not IsUpdatableBuild(), "dev", If(settings.AutoUpdate, "idle", "off"))
    End Sub

    Public Shared Function CurrentVersion() As String
        Dim v = GetType(Updater).Assembly.GetName().Version
        Return If(v Is Nothing, "0.0.0", $"{v.Major}.{v.Minor}.{v.Build}")
    End Function

    ''' <summary>A published single-file exe (no AgenticClient.dll next to it).</summary>
    Public Shared Function IsUpdatableBuild() As Boolean
        Return String.IsNullOrEmpty(GetType(Updater).Assembly.Location) AndAlso Environment.ProcessPath IsNot Nothing AndAlso
               (Not OperatingSystem.IsWindows() OrElse Environment.ProcessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
    End Function

    Public Function Status() As UpdateStatus
        _status.AutoUpdate = _settings.AutoUpdate
        If _status.State = "off" OrElse _status.State = "idle" Then _status.State = If(_settings.AutoUpdate, "idle", "off")
        Return _status
    End Function

    ''' <summary>Removes what earlier updates left: old exes that no longer run, unfinished downloads.</summary>
    Public Shared Sub CleanUp()
        If Not IsUpdatableBuild() Then Return
        Try
            Dim exe = Environment.ProcessPath
            Dim dir = Path.GetDirectoryName(exe)
            For Each f In Directory.GetFiles(dir, Path.GetFileName(exe) & ".old-*").Concat(Directory.GetFiles(dir, Path.GetFileName(exe) & ".new-*"))
                TryDelete(f)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub

    ''' <summary>At start: one check (at most a few seconds, so the window is not held up), then one every CheckIntervalMinutes.</summary>
    Public Async Function StartAsync() As Task
        If Not IsUpdatableBuild() OrElse Not _settings.AutoUpdate OrElse Not _settings.HasKey() Then Return
        Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(5))
            Try
                Await CheckAsync(install:=True, ct:=cts.Token, filesOnlyBeforeLoad:=True).ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
        End Using
        _timer = New Timer(Sub(state)
                               Dim unused = PeriodicAsync()
                           End Sub, Nothing, _interval, _interval)
    End Function

    Private Async Function PeriodicAsync() As Task
        If Not _settings.AutoUpdate OrElse _status.State = "ready" Then Return
        Try
            Await CheckAsync(install:=True, ct:=CancellationToken.None).ConfigureAwait(False)
        Catch ex As OperationCanceledException
        End Try
    End Function

    ''' <summary>
    ''' Asks the server; syncs www\desktop when the exe is current (a new exe brings its own files on restart) and, with
    ''' install, downloads and installs a newer exe. filesOnlyBeforeLoad: the exe download continues in the background.
    ''' </summary>
    Public Async Function CheckAsync(install As Boolean, ct As CancellationToken, Optional filesOnlyBeforeLoad As Boolean = False) As Task(Of UpdateStatus)
        If Not IsUpdatableBuild() Then
            _status.State = "dev"
            _status.Reason = "This is a development build; it does not update itself."
            Return _status
        End If
        If Not Await _gate.WaitAsync(0).ConfigureAwait(False) Then Return _status
        Dim release = True
        Dim toInstall As JsonElement? = Nothing
        Try
            If _status.State = "ready" Then Return _status
            _status.State = "checking"
            _status.Error = Nothing
            Using http = Client(TimeSpan.FromSeconds(30))
                Dim q = $"/api/updates/{App}?version={Uri.EscapeDataString(_status.CurrentVersion)}&sha256={SelfSha()}" &
                        $"&installId={Uri.EscapeDataString(If(_settings.DeviceId, ""))}&machine={Uri.EscapeDataString(Environment.MachineName)}"
                Dim root As JsonElement
                Using res = Await http.GetAsync(_settings.NormalizedBaseUrl() & q, ct).ConfigureAwait(False)
                    _status.LastCheckUtc = DateTime.UtcNow
                    If res.StatusCode = Net.HttpStatusCode.NotFound OrElse res.StatusCode = Net.HttpStatusCode.Conflict Then
                        _status.State = "none"
                        _status.Reason = "The server publishes no desktop app build."
                        Return _status
                    End If
                    Dim body = Await res.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not res.IsSuccessStatusCode Then Throw New HttpRequestException($"The server answered {CInt(res.StatusCode)}.")
                    Using doc = JsonDocument.Parse(body)
                        root = doc.RootElement.Clone()
                    End Using
                End Using
                Dim latest = root.GetProperty("latest")
                _status.LatestVersion = Str(latest, "version")
                _status.Notes = Str(latest, "notes")
                _status.Reason = Str(root, "reason")
                Dim minutes = Int(root, "checkIntervalMinutes", 360)
                If minutes >= 5 AndAlso TimeSpan.FromMinutes(minutes) <> _interval Then
                    _interval = TimeSpan.FromMinutes(minutes)
                    _timer?.Change(_interval, _interval)
                End If
                Dim available = Bool(root, "updateAvailable")
                If Not available Then
                    _status.FilesUpdated += Await SyncFilesAsync(http, latest, ct).ConfigureAwait(False)
                    _status.State = "current"
                    Return _status
                End If
                If Not install Then
                    _status.State = "available"
                    Return _status
                End If
                If filesOnlyBeforeLoad Then
                    ' Do not hold the window up for the download: it continues in the background.
                    release = False
                    Dim unused = Task.Run(Async Function()
                                              Try
                                                  Using bg = Client(TimeSpan.FromMinutes(30))
                                                      Await InstallAsync(bg, latest, CancellationToken.None).ConfigureAwait(False)
                                                  End Using
                                              Catch ex As Exception
                                                  Fail(ex)
                                              Finally
                                                  _gate.Release()
                                              End Try
                                          End Function)
                    Return _status
                End If
                toInstall = latest
            End Using
            Using http = Client(TimeSpan.FromMinutes(30))
                Await InstallAsync(http, toInstall, ct).ConfigureAwait(False)
            End Using
            Return _status
        Catch ex As OperationCanceledException When ct.IsCancellationRequested
            If _status.State = "checking" Then _status.State = "idle"
            Throw
        Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is KeyNotFoundException OrElse TypeOf ex Is TaskCanceledException
            Fail(ex)
            Return _status
        Finally
            If release Then _gate.Release()
        End Try
    End Function

    ''' <summary>Downloads the server's exe, checks it and swaps it in. latest = Nothing asks the server again.</summary>
    Public Async Function InstallAsync(http As HttpClient, latest As JsonElement?, ct As CancellationToken) As Task
        If latest Is Nothing Then
            Using res = Await http.GetAsync(_settings.NormalizedBaseUrl() & $"/api/updates/{App}?version={Uri.EscapeDataString(_status.CurrentVersion)}&sha256={SelfSha()}", ct).ConfigureAwait(False)
                res.EnsureSuccessStatusCode()
                Using doc = JsonDocument.Parse(Await res.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
                    If Not Bool(doc.RootElement, "updateAvailable") Then
                        _status.State = "current"
                        Return
                    End If
                    latest = doc.RootElement.GetProperty("latest").Clone()
                End Using
            End Using
        End If
        Dim info = latest.Value
        Dim wantSha = Str(info, "sha256").ToLowerInvariant()
        Dim wantSize = Lng(info, "size")
        Dim temp = _exe & ".new-" & Environment.ProcessId.ToString()
        _status.State = "downloading"
        _status.Progress = 0
        Try
            Using res = Await http.GetAsync(_settings.NormalizedBaseUrl() & Str(info, "downloadUrl"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                res.EnsureSuccessStatusCode()
                Dim total = If(res.Content.Headers.ContentLength, wantSize)
                Using source = Await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(False),
                      target As New FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync:=True),
                      hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    Dim buffer(81919) As Byte
                    Dim done = 0L
                    While True
                        Dim n = Await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(False)
                        If n = 0 Then Exit While
                        Await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(False)
                        hash.AppendData(buffer, 0, n)
                        done += n
                        If total > 0 Then _status.Progress = CInt(Math.Min(100, done * 100 \ total))
                    End While
                    Dim got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()
                    If got <> wantSha OrElse (wantSize > 0 AndAlso done <> wantSize) Then Throw New InvalidDataException("The download did not match the server's SHA-256; nothing was changed.")
                End Using
            End Using
            PlatformUtil.CopyFileMode(_exe, temp)
            Dim old = _exe & ".old-" & DateTime.UtcNow.Ticks.ToString()
            File.Move(_exe, old)
            Try
                File.Move(temp, _exe)
            Catch
                File.Move(old, _exe)
                Throw
            End Try
            _selfSha = wantSha
            _status.State = "ready"
            _status.LatestVersion = Str(info, "version")
            _status.Progress = 100
        Finally
            TryDelete(temp)
        End Try
    End Function

    ''' <summary>Makes the app's files (www\desktop) match the package; returns how many were written or removed.</summary>
    Private Async Function SyncFilesAsync(http As HttpClient, latest As JsonElement, ct As CancellationToken) As Task(Of Integer)
        Dim changed = 0
        Dim wanted As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim files As JsonElement
        If Not latest.TryGetProperty("files", files) OrElse files.ValueKind <> JsonValueKind.Array Then Return 0
        For Each f In files.EnumerateArray()
            Dim rel = Str(f, "path")
            Dim local = LocalPath(rel)
            If local Is Nothing Then Continue For
            wanted.Add(local)
            Dim sha = Str(f, "sha256").ToLowerInvariant()
            If File.Exists(local) AndAlso HashOf(local) = sha Then Continue For
            Directory.CreateDirectory(Path.GetDirectoryName(local))
            Dim bytes = Await http.GetByteArrayAsync(_settings.NormalizedBaseUrl() & Str(f, "url"), ct).ConfigureAwait(False)
            If Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() <> sha Then Throw New InvalidDataException($"{rel} did not match the server's SHA-256.")
            Dim temp = local & ".tmp-" & Environment.ProcessId.ToString()
            Await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(False)
            ReplaceFile(temp, local)
            changed += 1
        Next
        ' Files the package no longer has (only in folders it manages, e.g. www\desktop).
        Dim folders As JsonElement
        If latest.TryGetProperty("managedFolders", folders) AndAlso folders.ValueKind = JsonValueKind.Array Then
            For Each d In folders.EnumerateArray()
                Dim dir = LocalPath(d.GetString())
                If dir Is Nothing OrElse Not Directory.Exists(dir) Then Continue For
                For Each f In Directory.GetFiles(dir)
                    If wanted.Contains(f) OrElse f.Contains(".tmp-", StringComparison.Ordinal) Then Continue For
                    TryDelete(f)
                    changed += 1
                Next
            Next
        End If
        Return changed
    End Function

    ''' <summary>A package path inside the app's folder, or Nothing for anything outside it or a private file.</summary>
    Private Function LocalPath(rel As String) As String
        If String.IsNullOrWhiteSpace(rel) OrElse rel.Contains("..") OrElse Path.IsPathRooted(rel) OrElse rel.Contains(":"c) Then Return Nothing
        Dim full = Path.GetFullPath(Path.Combine(_root, rel.Replace("/"c, Path.DirectorySeparatorChar)))
        If Not full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Dim name = Path.GetFileName(full)
        If name.Equals("config.json", StringComparison.OrdinalIgnoreCase) OrElse name.Equals("mcp.json", StringComparison.OrdinalIgnoreCase) OrElse
           name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) OrElse name.Equals(Path.GetFileName(_exe), StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Return full
    End Function

    ''' <summary>Replaces a file the local server may be reading at that moment (it retries briefly).</summary>
    Private Shared Sub ReplaceFile(temp As String, target As String)
        For attempt = 1 To 10
            Try
                File.Move(temp, target, overwrite:=True)
                Return
            Catch ex As IOException When attempt < 10
                Thread.Sleep(100)
            End Try
        Next
    End Sub

    ''' <summary>Starts the (new) exe once this one has closed, so it gets the same local port and browser data.</summary>
    ''' <remarks>It gets this instance's own arguments (e.g. --no-window on Linux), without an earlier --wait-for.</remarks>
    Public Sub StartNewInstance()
        Dim psi As New ProcessStartInfo(_exe) With {.UseShellExecute = False, .WorkingDirectory = _root}
        psi.ArgumentList.Add("--wait-for")
        psi.ArgumentList.Add(Environment.ProcessId.ToString())
        Dim original = Environment.GetCommandLineArgs().Skip(1).ToList()
        Dim i = 0
        While i < original.Count
            If original(i) = "--wait-for" Then
                i += 2
                Continue While
            End If
            psi.ArgumentList.Add(original(i))
            i += 1
        End While
        Process.Start(psi)?.Dispose()
    End Sub

    ' ------------------------------------------------------------------ helpers

    Private Function Client(timeout As TimeSpan) As HttpClient
        Dim http As New HttpClient(New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(8), .AutomaticDecompression = Net.DecompressionMethods.All}) With {.Timeout = timeout}
        If _settings.HasKey() Then http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " & _settings.ApiKey)
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "agentic-desktop/" & _status.CurrentVersion)
        Return http
    End Function

    Private Function SelfSha() As String
        If _selfSha Is Nothing Then
            Try
                _selfSha = HashOf(_exe)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                _selfSha = ""
            End Try
        End If
        Return _selfSha
    End Function

    Private Shared Function HashOf(file As String) As String
        Using s As New FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 16)
            Return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()
        End Using
    End Function

    Private Sub Fail(ex As Exception)
        _status.State = "error"
        _status.Error = ex.Message
    End Sub

    Private Shared Sub TryDelete(f As String)
        Try
            If File.Exists(f) Then File.Delete(f)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub

    Private Shared Function Str(e As JsonElement, name As String) As String
        Dim v As JsonElement
        Return If(e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String, v.GetString(), "")
    End Function

    Private Shared Function Bool(e As JsonElement, name As String) As Boolean
        Dim v As JsonElement
        Return e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.True
    End Function

    Private Shared Function Int(e As JsonElement, name As String, fallback As Integer) As Integer
        Dim v As JsonElement
        Dim n As Integer
        Return If(e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt32(n), n, fallback)
    End Function

    Private Shared Function Lng(e As JsonElement, name As String) As Long
        Dim v As JsonElement
        Dim n As Long
        Return If(e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt64(n), n, 0L)
    End Function

End Class
