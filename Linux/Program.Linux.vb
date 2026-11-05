' The Linux build (dotnet publish -r linux-x64): the same app without WinForms and WebView2. Its window is a browser app
' window (LinuxHost); the Windows build starts in Program.vb.
Friend Module Program

    Friend Function Main(args As String()) As Integer
        Return LinuxHost.Run(args)
    End Function

End Module
