''' <summary>What every start does first, on Windows and on Linux.</summary>
Friend Module Startup

    ''' <summary>
    ''' After an update the old instance starts the new one with --wait-for &lt;pid&gt;: wait until it has closed, so this
    ''' instance gets the same local port (the web app's saved layout and theme) and the browser data folder.
    ''' </summary>
    Public Sub WaitForPrevious(args As String())
        Dim i = Array.IndexOf(args, "--wait-for")
        Dim pid As Integer
        If i >= 0 AndAlso i + 1 < args.Length AndAlso Integer.TryParse(args(i + 1), pid) Then
            Try
                Using previous = Process.GetProcessById(pid)
                    previous.WaitForExit(20000)
                End Using
            Catch ex As ArgumentException
                ' Already gone.
            End Try
        End If
    End Sub

End Module
