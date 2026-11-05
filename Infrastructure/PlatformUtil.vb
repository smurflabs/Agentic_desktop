Imports System.IO
Imports System.Runtime.InteropServices

''' <summary>
''' What differs between the Windows and the Linux build: the update channel, finding programs on PATH, opening folders
''' and keeping a swapped-in binary executable.
''' </summary>
Friend NotInheritable Class PlatformUtil

    Private Sub New()
    End Sub

    ''' <summary>"win-x64", "linux-x64", "linux-arm64" …: the platform this build runs on.</summary>
    Public Shared Function Rid() As String
        Dim os = If(OperatingSystem.IsWindows(), "win", If(OperatingSystem.IsMacOS(), "osx", "linux"))
        Return os & "-" & RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    End Function

    ''' <summary>"windows" or "linux" (what desktop.js adapts its pages to).</summary>
    Public Shared Function Name() As String
        Return If(OperatingSystem.IsWindows(), "windows", If(OperatingSystem.IsMacOS(), "macos", "linux"))
    End Function

    ''' <summary>The server's update channel: "desktop" for Windows, "desktop-linux-x64" for Linux, so each gets its own build.</summary>
    Public Shared Function UpdateApp(baseApp As String) As String
        Return If(OperatingSystem.IsWindows(), baseApp, baseApp & "-" & Rid())
    End Function

    ''' <summary>A program on PATH, or Nothing. Windows tries the PATHEXT endings (.exe, .cmd …); elsewhere the name itself.</summary>
    Public Shared Function Which(program As String) As String
        If String.IsNullOrWhiteSpace(program) Then Return Nothing
        Dim candidates As New List(Of String)
        If OperatingSystem.IsWindows() Then
            Dim exts = If(Environment.GetEnvironmentVariable("PATHEXT"), ".COM;.EXE;.BAT;.CMD").Split(";"c, StringSplitOptions.RemoveEmptyEntries)
            If Path.HasExtension(program) Then candidates.Add(program)
            candidates.AddRange(exts.Select(Function(e) program & e.ToLowerInvariant()))
        Else
            candidates.Add(program)
        End If
        If program.Contains("\"c) OrElse program.Contains("/"c) Then Return candidates.FirstOrDefault(Function(c) IsRunnable(c))
        For Each folder As String In If(Environment.GetEnvironmentVariable("PATH"), "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            For Each c In candidates
                Try
                    Dim full = Path.Combine(folder.Trim(""""c), c)
                    If IsRunnable(full) Then Return full
                Catch ex As ArgumentException
                End Try
            Next
        Next
        Return Nothing
    End Function

    Private Shared Function IsRunnable(file As String) As Boolean
        If Not IO.File.Exists(file) Then Return False
        If OperatingSystem.IsWindows() Then Return True
        Try
            Return (IO.File.GetUnixFileMode(file) And (UnixFileMode.UserExecute Or UnixFileMode.GroupExecute Or UnixFileMode.OtherExecute)) <> 0
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return False
        End Try
    End Function

    ''' <summary>Gives target the file mode of model, executable (an exe swapped in by an update); nothing on Windows.</summary>
    Public Shared Sub CopyFileMode(model As String, target As String)
        If OperatingSystem.IsWindows() Then Return
        Dim mode = UnixFileMode.UserRead Or UnixFileMode.UserWrite Or UnixFileMode.UserExecute Or UnixFileMode.GroupRead Or
                   UnixFileMode.GroupExecute Or UnixFileMode.OtherRead Or UnixFileMode.OtherExecute
        Try
            If IO.File.Exists(model) Then mode = IO.File.GetUnixFileMode(model) Or UnixFileMode.UserExecute
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        IO.File.SetUnixFileMode(target, mode)
    End Sub

    ''' <summary>Only this account may read or write the file (Linux; Windows keeps its own access rules).</summary>
    Public Shared Sub MakePrivate(file As String)
        If OperatingSystem.IsWindows() OrElse Not IO.File.Exists(file) Then Return
        IO.File.SetUnixFileMode(file, UnixFileMode.UserRead Or UnixFileMode.UserWrite)
    End Sub

    ''' <summary>Shows a folder in the file manager: Explorer on Windows, xdg-open (or gio) on Linux.</summary>
    Public Shared Sub OpenFolder(folder As String)
        Dim psi As ProcessStartInfo
        If OperatingSystem.IsWindows() Then
            psi = New ProcessStartInfo("explorer.exe")
        Else
            Dim opener = If(Which("xdg-open"), Which("gio"))
            If opener Is Nothing Then Throw New InvalidOperationException("No file manager could be opened (xdg-open is not installed). The folder is " & folder)
            psi = New ProcessStartInfo(opener)
            If Path.GetFileName(opener) = "gio" Then psi.ArgumentList.Add("open")
        End If
        psi.UseShellExecute = False
        psi.ArgumentList.Add(folder)
        Process.Start(psi)?.Dispose()
    End Sub

    ''' <summary>Shows a file selected in its folder (Explorer on Windows); its folder in the file manager on Linux. The file is never run.</summary>
    Public Shared Sub ShowInFolder(file As String)
        If Not OperatingSystem.IsWindows() Then
            OpenFolder(Path.GetDirectoryName(file))
            Return
        End If
        ' Windows paths cannot contain quotes, so the quoted path is one argument.
        Process.Start(New ProcessStartInfo("explorer.exe", "/select,""" & file & """") With {.UseShellExecute = False})?.Dispose()
    End Sub

    ''' <summary>Opens a web page in the default browser.</summary>
    Public Shared Sub OpenUrl(url As String)
        If OperatingSystem.IsWindows() Then
            Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})?.Dispose()
        Else
            OpenFolder(url)
        End If
    End Sub

    ''' <summary>Opens a file in its app (Notepad when Windows has none for it); xdg-open on Linux.</summary>
    Public Shared Sub OpenFile(file As String)
        If OperatingSystem.IsWindows() Then
            Try
                Process.Start(New ProcessStartInfo(file) With {.UseShellExecute = True})?.Dispose()
            Catch ex As ComponentModel.Win32Exception
                Dim psi As New ProcessStartInfo("notepad.exe") With {.UseShellExecute = False}
                psi.ArgumentList.Add(file)
                Process.Start(psi)?.Dispose()
            End Try
            Return
        End If
        OpenFolder(file)
    End Sub

End Class
