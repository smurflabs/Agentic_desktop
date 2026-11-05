Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.RegularExpressions

' Claude Code's permission rules for the tools on this computer: "permissions": {"allow", "ask", "deny", "additionalDirectories"}
' in ~/.claude/settings.json, ~/.agentic/settings.json and each workspace folder's .claude/settings(.local).json and
' .agentic/settings(.local).json. They go to the Agentic server with each message in its rule format (deny blocks, ask asks
' in Auto mode too, allow never asks); deny is also checked here before a tool runs. A path outside the workspace folders
' (and additionalDirectories) is not refused: the chat page asks the user (this path, its folder, or no).

''' <summary>A path outside the folders the tools may use: the chat page asks the user.</summary>
Public Class OutsideWorkspaceException
    Inherits UnauthorizedAccessException

    Public ReadOnly Property FullPath As String

    Public Sub New(full As String, roots As IEnumerable(Of String))
        MyBase.New($"{full} is outside the workspace folders ({String.Join("; ", roots)}). The user was asked whether to allow it; if they did not, work inside the workspace.")
        FullPath = full
    End Sub

    ''' <summary>The path in such a message (the tool's result text), or Nothing.</summary>
    Public Shared Function PathIn(message As String) As String
        Dim m = Regex.Match(If(message, ""), "^(.+?) is outside the workspace folders \(")
        Return If(m.Success, m.Groups(1).Value, Nothing)
    End Function
End Class

Partial Public Class LocalTools

    Private ReadOnly _allowedPaths As New Concurrent.ConcurrentDictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>The user allowed a path outside the workspace (a file, or a folder and what is in it) while this app runs.</summary>
    Public Sub AllowPath(p As String)
        _allowedPaths(IO.Path.GetFullPath(p)) = True
    End Sub

    Private Shared ReadOnly ToolMap As New Dictionary(Of String, String()) From {
        {"Bash", {"run_command", "run_command_background", "monitor"}}, {"Read", {"read_file", "list_dir", "glob_files", "grep"}},
        {"Edit", {"edit_file", "edit_notebook", "write_file"}}, {"MultiEdit", {"edit_file"}}, {"Write", {"write_file"}}, {"NotebookEdit", {"edit_notebook"}},
        {"Glob", {"glob_files"}}, {"Grep", {"grep"}}, {"LS", {"list_dir"}}, {"WebFetch", {"read_page"}}, {"WebSearch", {"search_web"}}}
    Private Shared ReadOnly PathTools As New HashSet(Of String) From {"Read", "Edit", "MultiEdit", "Write", "NotebookEdit", "Glob", "Grep", "LS"}

    ''' <summary>One Claude Code rule as rules of the Agentic server ("Bash(npm test:*)" → "run_command(npm test*)").</summary>
    Public Shared Function TranslateRule(rule As String) As List(Of String)
        Dim out As New List(Of String)
        Dim m = Regex.Match(If(rule, ""), "^\s*([^()\s]+)\s*(?:\((.*)\))?\s*$")
        If Not m.Success Then Return out
        Dim tool = m.Groups(1).Value, spec = m.Groups(2).Value.Trim()
        If tool.StartsWith("mcp__", StringComparison.Ordinal) Then
            Dim parts = tool.Split({"__"}, StringSplitOptions.None)
            out.Add(If(parts.Length = 2 OrElse (parts.Length = 3 AndAlso parts(2).Length = 0), $"mcp__{parts(1)}__*", tool))
            Return out
        End If
        Dim names As String() = Nothing
        If Not ToolMap.TryGetValue(tool, names) Then names = {tool}
        If spec.Length = 0 OrElse spec = "*" Then Return names.ToList()
        Dim patterns As New List(Of String)
        If tool = "Bash" Then
            patterns.Add(If(spec.EndsWith(":*", StringComparison.Ordinal), spec.Substring(0, spec.Length - 2) & "*", spec))
        ElseIf tool = "WebFetch" Then
            patterns.Add(If(spec.StartsWith("domain:", StringComparison.Ordinal), "*" & spec.Substring(7) & "*", spec))
        ElseIf PathTools.Contains(tool) Then
            Dim p = Regex.Replace(spec, "\*\*/?", "*")
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace("\"c, "/"c)
            If p.StartsWith("//", StringComparison.Ordinal) Then
                p = p.Substring(1)
            ElseIf p.StartsWith("~/", StringComparison.Ordinal) Then
                p = home & p.Substring(1)
            ElseIf p.StartsWith("./", StringComparison.Ordinal) Then
                p = p.Substring(2)
            ElseIf p.StartsWith("/", StringComparison.Ordinal) AndAlso Not Regex.IsMatch(p, "^/[A-Za-z]:") Then
                p = p.Substring(1)
            End If
            Dim absolute = IO.Path.IsPathRooted(p) OrElse Regex.IsMatch(p, "^[A-Za-z]:")
            Dim back = p.Replace("/"c, "\"c)
            For Each x In If(absolute OrElse p.StartsWith("*", StringComparison.Ordinal), {p, back}, {p, "*/" & p, back, "*\" & back})
                If Not patterns.Contains(x) Then patterns.Add(x)
            Next
        Else
            patterns.Add(spec)
        End If
        For Each n In names
            For Each p In patterns
                out.Add($"{n}({p})")
            Next
        Next
        Return out
    End Function

    ''' <summary>The settings files with permission rules, most general first.</summary>
    Private Function RuleFiles() As List(Of String)
        Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        Dim list As New List(Of String) From {IO.Path.Combine(PathsUtil.ClaudeHome(), "settings.json"), IO.Path.Combine(home, ".agentic", "settings.json")}
        For Each r In _settings.EffectiveWorkspaces()
            For Each d In {".claude", ".agentic"}
                list.Add(IO.Path.Combine(r, d, "settings.json"))
                list.Add(IO.Path.Combine(r, d, "settings.local.json"))
            Next
        Next
        Return list
    End Function

    ''' <summary>Every rule of the settings files in the server's format, and the extra folders (additionalDirectories).</summary>
    Public Function PermissionRules() As (Allow As List(Of String), Ask As List(Of String), Deny As List(Of String), Dirs As List(Of String))
        Dim allow As New List(Of String), ask As New List(Of String), deny As New List(Of String), dirs As New List(Of String)
        Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        For Each file In RuleFiles()
            Dim text = PathsUtil.ReadFileSafe(file)
            If String.IsNullOrWhiteSpace(text) Then Continue For
            Try
                Using doc = JsonDocument.Parse(text, New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Dim p As JsonElement
                    If doc.RootElement.ValueKind <> JsonValueKind.Object OrElse Not doc.RootElement.TryGetProperty("permissions", p) OrElse p.ValueKind <> JsonValueKind.Object Then Continue For
                    For Each kind In {("allow", allow), ("ask", ask), ("deny", deny)}
                        Dim el As JsonElement
                        If p.TryGetProperty(kind.Item1, el) AndAlso el.ValueKind = JsonValueKind.Array Then
                            For Each r In el.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String)
                                kind.Item2.AddRange(TranslateRule(r.GetString()))
                            Next
                        End If
                    Next
                    Dim dEl As JsonElement
                    If p.TryGetProperty("additionalDirectories", dEl) AndAlso dEl.ValueKind = JsonValueKind.Array Then
                        Dim baseDir = IO.Path.GetDirectoryName(IO.Path.GetDirectoryName(file))
                        For Each d In dEl.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String)
                            Dim s = Regex.Replace(d.GetString(), "^~(?=$|[\\/])", home.Replace("$", "$$"))
                            Try
                                dirs.Add(IO.Path.GetFullPath(IO.Path.Combine(baseDir, s)))
                            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is PathTooLongException
                            End Try
                        Next
                    End If
                End Using
            Catch ex As JsonException
            End Try
        Next
        Return (allow.Distinct().Take(200).ToList(), ask.Distinct().Take(200).ToList(), deny.Distinct().Take(200).ToList(), dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
    End Function

    ''' <summary>What a call is about (the server's ToolTarget): its command, file, URL or query.</summary>
    Public Shared Function CallTarget(args As JsonElement) As String
        If args.ValueKind <> JsonValueKind.Object Then Return Nothing
        For Each k In {"command", "path", "file_path", "notebook_path", "url", "query", "pattern"}
            Dim el As JsonElement
            If args.TryGetProperty(k, el) AndAlso el.ValueKind = JsonValueKind.String AndAlso el.GetString().Length > 0 Then Return el.GetString()
        Next
        Return Nothing
    End Function

    Private Shared Function RuleGlob(pattern As String, value As String) As Boolean
        Dim re = "^" & Regex.Escape(pattern.Trim()).Replace("\*", ".*").Replace("\?", ".") & "$"
        Return Regex.IsMatch(value, re, RegexOptions.IgnoreCase Or RegexOptions.Singleline)
    End Function

    ''' <summary>The first rule of the list that matches the call (the server's matching), or Nothing.</summary>
    Public Shared Function MatchingRule(rules As IEnumerable(Of String), tool As String, args As JsonElement) As String
        Dim target = CallTarget(args)
        For Each rule In rules
            Dim m = Regex.Match(rule, "^\s*([^()\s]+)\s*(?:\((.*)\))?\s*$")
            If Not m.Success OrElse Not RuleGlob(m.Groups(1).Value, If(tool, "")) Then Continue For
            If m.Groups(2).Success AndAlso m.Groups(2).Value.Length > 0 AndAlso (target Is Nothing OrElse Not RuleGlob(m.Groups(2).Value, target)) Then Continue For
            Return rule
        Next
        Return Nothing
    End Function

    ''' <summary>Whether a path is one the tools may use: in a workspace folder, an extra folder, one the user allowed, or anywhere (setting).</summary>
    Private Function MayUsePath(full As String, roots As List(Of String)) As Boolean
        If _settings.WholeComputer OrElse roots.Any(Function(r) PathsUtil.IsInside(full, r)) Then Return True
        If PermissionRules().Dirs.Any(Function(d) PathsUtil.IsInside(full, d) OrElse String.Equals(IO.Path.GetFullPath(d), full, StringComparison.OrdinalIgnoreCase)) Then Return True
        Return _allowedPaths.Keys.Any(Function(a) String.Equals(a, full, StringComparison.OrdinalIgnoreCase) OrElse PathsUtil.IsInside(full, a))
    End Function

End Class
