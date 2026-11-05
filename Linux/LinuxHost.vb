Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports AgenticClient.Http

''' <summary>
''' Agentic Desktop on Linux. The same local server as on Windows (the web app with your API key added, the desktop layer,
''' skills, local MCP servers and tools on this computer, updates); the window is a browser app window
''' (BrowserWindow). The app quits when that window closes, on Ctrl+C or SIGTERM, and restarts itself after an update.
''' Options: --no-window (only the local server, e.g. on a machine without a desktop), --version.
''' </summary>
Friend NotInheritable Class LinuxHost

    Private Sub New()
    End Sub

    Public Shared Function Run(args As String()) As Integer
        If args.Contains("--version") OrElse args.Contains("-v") Then
            Console.WriteLine($"{Updater.CurrentVersion()} (Agentic Desktop, {PlatformUtil.Rid()})")
            Return 0
        End If
        If args.Contains("--help") OrElse args.Contains("-h") Then
            Console.WriteLine("Agentic Desktop " & Updater.CurrentVersion() & vbLf & vbLf &
                              "  AgenticClient              start and open the window (a Chrome, Chromium, Edge, Brave or Vivaldi app window," & vbLf &
                              "                             else your default browser)" & vbLf &
                              "  AgenticClient --no-window  only the local server; open the address it prints in a browser" & vbLf &
                              "  AgenticClient --version" & vbLf & vbLf &
                              "Environment: AGENTIC_CLIENT_BROWSER (browser to use), AGENTIC_CLIENT_HOME (data folder, default ~/.config/AgenticClient)," & vbLf &
                              "AGENTIC_CLIENT_SERVER and AGENTIC_CLIENT_API_KEY (server and key).")
            Return 0
        End If
        Startup.WaitForPrevious(args)
        Updater.CleanUp()

        Dim settings = AppSettingsStore.Load()
        Dim skills As New SkillStore(settings)
        Dim mcp As New Mcp.McpHost()
        ' The page the user runs opens in the browser (the app window has no preview pane here).
        Dim tools As New LocalTools(settings) With {.Checkpoints = New FileCheckpoints(IO.Path.Combine(PathsUtil.AppDataRoot(), "checkpoints")),
                                                    .ShowPreview = Sub(page As String, title As String) PlatformUtil.OpenUrl(page)}
        Dim updates As New Updater(settings)
        Dim quit As New ManualResetEventSlim(False)
        Dim restart = False
        Dim local As New LocalApi(skills, mcp, tools, settings) With {
            .Updates = updates,
            .RestartRequested = Sub()
                                    restart = True
                                    quit.Set()
                                End Sub,
            .CaptureScreen = If(LinuxScreenshot.Available(), New Func(Of String, Task(Of Byte()))(AddressOf LinuxScreenshot.CaptureAsync), Nothing),
            .Computer = New LinuxComputer(), .ScreenMap = Function() CType((0, 0, 1.0), (X As Integer, Y As Integer, Scale As Double)?),
            .HotkeyProblems = Function() New List(Of String)}
        Dim server As New EmbeddedServer(New ApiProxy(settings), local, settings, Guid.NewGuid().ToString("N"))
        Dim baseUrl As String
        Try
            baseUrl = server.Start(settings.Port).TrimEnd("/"c)
        Catch ex As Exception When TypeOf ex Is Net.HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is Net.Sockets.SocketException
            Console.Error.WriteLine("Agentic Desktop could not start its local server: " & ex.Message)
            Return 1
        End Try
        ' Local MCP servers connect in the background; www/desktop is brought up to date before the page loads.
        Dim unused = Task.Run(AddressOf mcp.ConnectAllAsync)
        Try
            updates.StartAsync().GetAwaiter().GetResult()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException
        End Try
        Dim start = If(String.IsNullOrWhiteSpace(settings.StartPage), "/chat", settings.StartPage)
        Dim url = baseUrl & If(start.StartsWith("/"), start, "/" & start)
        Console.WriteLine($"Agentic Desktop {Updater.CurrentVersion()} is running at {baseUrl}")
        If Not settings.HasKey() Then Console.WriteLine("No API key yet: the window asks for one (web app: user menu → API key).")

        Dim headless = args.Contains("--no-window") OrElse Environment.GetEnvironmentVariable("AGENTIC_CLIENT_NO_WINDOW") = "1" OrElse
                       Environment.GetEnvironmentVariable("AGENTIC_CLIENT_MINIMIZED") = "1"
        Dim window As Process = Nothing
        If Not headless Then
            window = BrowserWindow.Open(url)
            If window Is Nothing Then Console.WriteLine("Opened " & url & " in your browser.")
        Else
            Console.WriteLine("Open " & url & " in a browser.")
        End If
        Using sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Sub(c)
                                                                             c.Cancel = True
                                                                             quit.Set()
                                                                         End Sub),
              sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Sub(c)
                                                                               c.Cancel = True
                                                                               quit.Set()
                                                                           End Sub),
              sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Sub(c)
                                                                             c.Cancel = True
                                                                             quit.Set()
                                                                         End Sub)
            If window IsNot Nothing Then
                Dim opened = DateTime.UtcNow
                Dim unusedWatch = Task.Run(Sub()
                                               Try
                                                   window.WaitForExit()
                                               Catch ex As InvalidOperationException
                                               End Try
                                               ' A browser that exits at once handed the window to one already running with this profile:
                                               ' that window's closing cannot be seen, so the app keeps running until Ctrl+C.
                                               If DateTime.UtcNow - opened > TimeSpan.FromSeconds(5) Then
                                                   quit.Set()
                                               Else
                                                   Console.WriteLine("The window opened in a browser that was already running. Ctrl+C quits.")
                                               End If
                                           End Sub)
            Else
                Console.WriteLine("Ctrl+C quits.")
            End If
            quit.Wait()
        End Using

        If restart Then
            Try
                updates.StartNewInstance()
            Catch ex As ComponentModel.Win32Exception
                Console.Error.WriteLine("The updated app could not be started: " & ex.Message)
            End Try
        End If
        ' The window would show a page without its server: it closes with the app (the new version opens its own).
        BrowserWindow.Close(window)
        server.Stop()
        mcp.Dispose()
        tools.StopBackgroundCommands()
        tools.StopRuns()
        Return 0
    End Function

End Class
