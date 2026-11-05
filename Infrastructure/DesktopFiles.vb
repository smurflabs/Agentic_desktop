Imports System.IO
Imports System.Reflection

''' <summary>
''' The desktop layer's files (www\desktop: the connect page, desktop.js, desktop.css) normally sit next to the exe. The exe
''' also carries a copy inside, so a lone exe (downloaded on its own, without its www folder) works too: on the first start
''' the files are written next to it, or into the data folder (%APPDATA%\AgenticClient\www) when that one is not writable.
''' </summary>
Friend Module DesktopFiles

    Private Const Marker As String = "desktop/connect.html"

    ''' <summary>The www folder to serve from, writing the embedded copy out first when no folder has the files.</summary>
    Public Function WwwRoot() As String
        Dim beside = Path.Combine(AppContext.BaseDirectory, "www")
        For Each folder In {beside, Path.Combine(PathsUtil.AppDataRoot(), "www")}
            If File.Exists(Path.Combine(folder, Marker)) Then Return folder
        Next
        For Each folder In {beside, Path.Combine(PathsUtil.AppDataRoot(), "www")}
            Try
                If Extract(folder) Then Return folder
            Catch
                ' Not writable (e.g. Program Files): try the next folder.
            End Try
        Next
        Return beside
    End Function

    Private Function Extract(folder As String) As Boolean
        Dim asm = Assembly.GetExecutingAssembly()
        ' LogicalName www/%(RecursiveDir)...: RecursiveDir uses "\" on Windows and "/" elsewhere.
        Dim names = asm.GetManifestResourceNames().Where(Function(n) n.StartsWith("www/", StringComparison.Ordinal)).ToList()
        If names.Count = 0 Then Return False
        For Each name In names
            Dim relative = name.Substring(4).Replace("\"c, "/"c)
            Dim target = Path.GetFullPath(Path.Combine(folder, relative.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not PathsUtil.IsInside(target, folder) Then Continue For
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            Using source = asm.GetManifestResourceStream(name), output = File.Create(target)
                source.CopyTo(output)
            End Using
        Next
        Return File.Exists(Path.Combine(folder, Marker))
    End Function

End Module
