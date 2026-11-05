Imports System.IO
Imports System.Text
Imports System.Threading

''' <summary>
''' Screenshots on Linux with the desktop's own tool: gnome-screenshot, spectacle (KDE), grim + slurp (Wayland),
''' xfce4-screenshooter, flameshot, scrot or ImageMagick's import, whichever is installed (Wayland desktops first try the
''' tools that work there). "region" lets the user drag an area; "screen" takes the whole screen.
''' </summary>
Friend NotInheritable Class LinuxScreenshot

    Private Sub New()
    End Sub

    Private Shared Function Wayland() As Boolean
        Return Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
    End Function

    ''' <summary>The tools to try, in order, for this kind of desktop.</summary>
    Private Shared Function Order() As String()
        If OperatingSystem.IsMacOS() Then Return {"screencapture"}
        Return If(Wayland(),
                  {"gnome-screenshot", "spectacle", "grim", "flameshot"},
                  {"gnome-screenshot", "spectacle", "xfce4-screenshooter", "flameshot", "scrot", "import"})
    End Function

    Public Shared Function Available() As Boolean
        Return Order().Any(Function(t) PlatformUtil.Which(t) IsNot Nothing)
    End Function

    ''' <summary>The screenshot as PNG, or Nothing when the user cancelled.</summary>
    Public Shared Async Function CaptureAsync(area As String) As Task(Of Byte())
        ' "monitor" and "all" are taken for the model (take_screenshot): never interactive.
        Dim region = area <> "screen" AndAlso area <> "monitor" AndAlso area <> "all"
        Dim tool = Order().FirstOrDefault(Function(t) PlatformUtil.Which(t) IsNot Nothing)
        If tool Is Nothing Then
            Throw New InvalidOperationException(If(OperatingSystem.IsMacOS(),
                "screencapture was not found (it ships with macOS).",
                "No screenshot tool is installed. Install gnome-screenshot, spectacle, grim and slurp, flameshot, scrot or ImageMagick."))
        End If
        Dim file = Path.Combine(Path.GetTempPath(), $"agentic-shot-{Guid.NewGuid():N}.png")
        Try
            Select Case tool
                Case "screencapture"
                    ' macOS: -x no sound; -i lets the user drag an area (or press space for a window).
                    Await RunAsync(tool, If(region, {"-i", "-x", file}, {"-x", file})).ConfigureAwait(False)
                Case "flameshot"
                    ' flameshot writes the PNG to its output.
                    Return Await RunForBytesAsync(tool, If(region, {"gui", "--raw"}, {"full", "--raw"})).ConfigureAwait(False)
                Case "grim"
                    If region Then
                        If PlatformUtil.Which("slurp") Is Nothing Then Throw New InvalidOperationException("Install slurp to choose an area (grim takes the screenshot).")
                        Dim geometry = Encoding.UTF8.GetString(If(Await RunForBytesAsync("slurp", Array.Empty(Of String)()).ConfigureAwait(False), Array.Empty(Of Byte)())).Trim()
                        If geometry.Length = 0 Then Return Nothing
                        Await RunAsync(tool, {"-g", geometry, file}).ConfigureAwait(False)
                    Else
                        Await RunAsync(tool, {file}).ConfigureAwait(False)
                    End If
                Case "gnome-screenshot" : Await RunAsync(tool, If(region, {"-a", "-f", file}, {"-f", file})).ConfigureAwait(False)
                Case "spectacle" : Await RunAsync(tool, If(region, {"-b", "-n", "-r", "-o", file}, {"-b", "-n", "-f", "-o", file})).ConfigureAwait(False)
                Case "xfce4-screenshooter" : Await RunAsync(tool, If(region, {"-r", "-s", file}, {"-f", "-s", file})).ConfigureAwait(False)
                Case "scrot" : Await RunAsync(tool, If(region, {"-s", file}, {file})).ConfigureAwait(False)
                Case "import" : Await RunAsync(tool, If(region, {file}, {"-window", "root", file})).ConfigureAwait(False)
            End Select
            Return If(IO.File.Exists(file) AndAlso New FileInfo(file).Length > 0, IO.File.ReadAllBytes(file), Nothing)
        Finally
            Try
                If IO.File.Exists(file) Then IO.File.Delete(file)
            Catch ex As IOException
            End Try
        End Try
    End Function

    ''' <summary>Runs the tool and waits (the user may take a while to choose an area); a cancelled tool just ends.</summary>
    Private Shared Async Function RunAsync(tool As String, args As String()) As Task
        Dim psi As New ProcessStartInfo(PlatformUtil.Which(tool)) With {.UseShellExecute = False}
        For Each a In args
            psi.ArgumentList.Add(a)
        Next
        Using p = Process.Start(psi), cts As New CancellationTokenSource(TimeSpan.FromMinutes(3))
            Try
                Await p.WaitForExitAsync(cts.Token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                p.Kill(True)
            End Try
        End Using
    End Function

    Private Shared Async Function RunForBytesAsync(tool As String, args As String()) As Task(Of Byte())
        Dim psi As New ProcessStartInfo(PlatformUtil.Which(tool)) With {.UseShellExecute = False, .RedirectStandardOutput = True}
        For Each a In args
            psi.ArgumentList.Add(a)
        Next
        Using p = Process.Start(psi), ms As New MemoryStream(), cts As New CancellationTokenSource(TimeSpan.FromMinutes(3))
            Try
                Await p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token).ConfigureAwait(False)
                Await p.WaitForExitAsync(cts.Token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                p.Kill(True)
                Return Nothing
            End Try
            Return If(p.ExitCode = 0 AndAlso ms.Length > 0, ms.ToArray(), Nothing)
        End Using
    End Function

End Class
