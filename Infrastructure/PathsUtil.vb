Imports System.IO
Imports System.Text

Friend Module PathsUtil

    ''' <summary>
    ''' %APPDATA%\AgenticClient (~/.config/AgenticClient on Linux) — settings, the key, MCP config, skills, commands and the
    ''' browser profile.
    ''' The environment variable AGENTIC_CLIENT_HOME moves it (portable installs, test profiles).
    ''' </summary>
    Public Function AppDataRoot() As String
        Dim custom = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_HOME")
        If Not String.IsNullOrWhiteSpace(custom) Then Return Path.GetFullPath(custom)
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgenticClient")
    End Function

    ''' <summary>%USERPROFILE%\.claude — Claude Code's skills, slash commands, plugins and settings.</summary>
    Public Function ClaudeHome() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
    End Function

    Public Function EnsureDir(path As String) As String
        Dim full = IO.Path.GetFullPath(path)
        If Not Directory.Exists(full) Then Directory.CreateDirectory(full)
        Return full
    End Function

    Public Function ReadFileSafe(path As String) As String
        Try
            Return If(File.Exists(path), File.ReadAllText(path, Encoding.UTF8), Nothing)
        Catch
            Return Nothing
        End Try
    End Function

    Public Function WriteFileSafe(path As String, content As String) As Boolean
        Try
            EnsureDir(IO.Path.GetDirectoryName(path))
            File.WriteAllText(path, content, New UTF8Encoding(False))
            Return True
        Catch
            Return False
        End Try
    End Function

    ''' <summary>True when path is root or inside it (both made absolute first; names are case-sensitive on Linux).</summary>
    Public Function IsInside(path As String, root As String) As Boolean
        If String.IsNullOrWhiteSpace(path) OrElse String.IsNullOrWhiteSpace(root) Then Return False
        Dim sep = IO.Path.DirectorySeparatorChar
        Dim p = IO.Path.GetFullPath(path).TrimEnd(sep) & sep
        Dim r = IO.Path.GetFullPath(root).TrimEnd(sep) & sep
        Return p.StartsWith(r, If(OperatingSystem.IsWindows(), StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal))
    End Function

End Module
