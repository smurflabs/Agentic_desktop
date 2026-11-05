Imports System.IO
Imports System.Runtime.InteropServices

''' <summary>
''' The Linux build's window: a Chromium-based browser in app mode (--app: no tabs or address bar) with a profile of its
''' own, so it is a separate window the app can watch (closing it quits the app), with the microphone and notifications
''' the web app asks for. Without such a browser the default browser opens the address. AGENTIC_CLIENT_BROWSER picks one.
''' </summary>
Friend NotInheritable Class BrowserWindow

    Private Shared ReadOnly Browsers As String() = {"google-chrome", "google-chrome-stable", "chromium", "chromium-browser",
                                                     "microsoft-edge", "microsoft-edge-stable", "brave-browser", "brave", "vivaldi"}

    ''' <summary>macOS Chromium-based browsers (app bundles are not on PATH): the binary inside each .app.</summary>
    Private Shared ReadOnly MacBrowsers As String() = {
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
        "/Applications/Vivaldi.app/Contents/MacOS/Vivaldi"}

    ''' <summary>A Chromium-based browser to open the app window with, or Nothing when none is found.</summary>
    Private Shared Function FindBrowser(custom As String) As String
        If Not String.IsNullOrWhiteSpace(custom) Then
            custom = custom.Trim()
            Return If(custom.Contains("/"c) AndAlso File.Exists(custom), custom, PlatformUtil.Which(custom))
        End If
        If OperatingSystem.IsMacOS() Then
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Return MacBrowsers.Concat(MacBrowsers.Select(Function(p) home & p)).FirstOrDefault(AddressOf File.Exists)
        End If
        Return Browsers.Select(AddressOf PlatformUtil.Which).FirstOrDefault(Function(x) x IsNot Nothing)
    End Function

    <DllImport("libc", EntryPoint:="kill", SetLastError:=True)>
    Private Shared Function SendSignal(pid As Integer, sig As Integer) As Integer
    End Function

    Private Sub New()
    End Sub

    ''' <summary>Opens url; returns the browser process of the app window, or Nothing when the default browser was used.</summary>
    Public Shared Function Open(url As String) As Process
        ' No desktop session (a Linux server, SSH): nothing to open a window on. macOS always has a window server for a user.
        If OperatingSystem.IsLinux() AndAlso String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) AndAlso String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) Then
            Console.WriteLine("No desktop session: open " & url & " in a browser (on this computer).")
            Return Nothing
        End If
        Dim custom = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_BROWSER")
        Dim exe = FindBrowser(custom)
        If exe IsNot Nothing Then
            Dim psi As New ProcessStartInfo(exe) With {.UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True}
            psi.ArgumentList.Add("--app=" & url)
            psi.ArgumentList.Add("--user-data-dir=" & ProfileDir(exe))
            psi.ArgumentList.Add("--class=AgenticDesktop")
            psi.ArgumentList.Add("--window-size=1440,900")
            psi.ArgumentList.Add("--no-first-run")
            psi.ArgumentList.Add("--no-default-browser-check")
            psi.ArgumentList.Add("--hide-crash-restore-bubble")
            ' Notification sounds play while the window is in the background (as in the Windows app).
            psi.ArgumentList.Add("--autoplay-policy=no-user-gesture-required")
            Dim debugPort As Integer
            If Integer.TryParse(Environment.GetEnvironmentVariable("AGENTIC_CLIENT_DEBUG_PORT"), debugPort) AndAlso debugPort > 0 Then psi.ArgumentList.Add("--remote-debugging-port=" & debugPort)
            Try
                Dim p = Process.Start(psi)
                ' The browser's own log lines are not the app's.
                AddHandler p.OutputDataReceived, Sub(s, e)
                                                 End Sub
                AddHandler p.ErrorDataReceived, Sub(s, e)
                                                End Sub
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                Return p
            Catch ex As ComponentModel.Win32Exception
                Console.Error.WriteLine($"{exe} could not be started: {ex.Message}")
            End Try
        End If
        Dim opener = PlatformUtil.Which(If(OperatingSystem.IsMacOS(), "open", "xdg-open"))
        If opener Is Nothing Then
            Console.WriteLine("No browser found (install Chrome or Chromium for an app window). Open " & url & " in a browser.")
            Return Nothing
        End If
        Try
            Dim psi As New ProcessStartInfo(opener) With {.UseShellExecute = False}
            psi.ArgumentList.Add(url)
            Process.Start(psi)?.Dispose()
        Catch ex As ComponentModel.Win32Exception
            Console.WriteLine("Open " & url & " in a browser.")
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' The window's own browser profile (cookies, permissions). A snap browser may only write in its snap folder, so it
    ''' gets ~/snap/&lt;name&gt;/common/agentic-desktop.
    ''' </summary>
    Private Shared Function ProfileDir(exe As String) As String
        If exe.StartsWith("/snap/", StringComparison.Ordinal) Then
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Return PathsUtil.EnsureDir(Path.Combine(home, "snap", Path.GetFileName(exe), "common", "agentic-desktop"))
        End If
        Return PathsUtil.EnsureDir(Path.Combine(PathsUtil.AppDataRoot(), "browser"))
    End Function

    ''' <summary>Closes the app window (SIGTERM, so the browser shuts down cleanly; killed if it is still there after 3 s).</summary>
    Public Shared Sub Close(window As Process)
        If window Is Nothing Then Return
        Try
            If window.HasExited Then Return
            SendSignal(window.Id, 15)
            If Not window.WaitForExit(3000) Then window.Kill(entireProcessTree:=True)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse
                                   TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
        End Try
    End Sub

End Class
