Imports System.Windows.Forms
Imports AgenticClient.Http

' The Windows build: a WinForms window with WebView2 (Form1). The Linux build starts in Linux\Program.Linux.vb instead.
Friend Module Program

    <STAThread>
    Friend Sub Main(args As String())
        Startup.WaitForPrevious(args)
        Updater.CleanUp()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
        Application.Run(New Form1())
    End Sub

End Module
