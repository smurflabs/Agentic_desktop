Imports System.IO
Imports System.IO.Compression
Imports System.Text

''' <summary>
''' Claude Code skills and slash commands from every place Claude Code keeps them:
'''   Desktop      %APPDATA%\AgenticClient\skills\&lt;skill&gt;\SKILL.md and \commands\*.md (created and edited here)
'''   Claude Code  %USERPROFILE%\.claude\skills and \.claude\commands (your personal Claude Code skills and commands)
'''   Plugins      %USERPROFILE%\.claude\plugins\cache\&lt;marketplace&gt;\&lt;plugin&gt;\&lt;version&gt;\skills|commands (named plugin:name)
'''   Folders      extra folders from the settings (a skills folder, or a project whose .claude\skills is used)
''' </summary>
Public Class SkillStore

    Private ReadOnly _settings As AppSettings
    Private ReadOnly _lock As New Object()
    Private _items As New List(Of SkillInfo)
    Private _roots As New List(Of Object)

    Public Sub New(settings As AppSettings)
        _settings = settings
        SeedSamples()
        Refresh()
    End Sub

    Public Shared Function DesktopRoot() As String
        Return PathsUtil.EnsureDir(Path.Combine(PathsUtil.AppDataRoot(), "skills"))
    End Function

    Public Shared Function DesktopCommandsRoot() As String
        Return PathsUtil.EnsureDir(Path.Combine(PathsUtil.AppDataRoot(), "commands"))
    End Function

    ''' <summary>The sample skills shipped next to the exe are copied once into an empty desktop skills folder.</summary>
    Private Shared Sub SeedSamples()
        Try
            Dim target = DesktopRoot()
            Dim marker = Path.Combine(PathsUtil.AppDataRoot(), ".samples-installed")
            Dim samples = Path.Combine(AppContext.BaseDirectory, "skills")
            If File.Exists(marker) OrElse Not Directory.Exists(samples) OrElse Directory.EnumerateDirectories(target).Any() Then Return
            For Each dir As String In Directory.EnumerateDirectories(samples)
                Dim dest = Path.Combine(target, Path.GetFileName(dir))
                Directory.CreateDirectory(dest)
                For Each f In Directory.EnumerateFiles(dir)
                    File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), False)
                Next
            Next
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"))
        Catch
        End Try
    End Sub

    Public Function Roots() As List(Of Object)
        SyncLock _lock
            Return _roots.ToList()
        End SyncLock
    End Function

    Public Sub Refresh()
        Dim items As New List(Of SkillInfo)
        Dim roots As New List(Of Object)
        Dim addRoot = Sub(source As String, folder As String, kind As String)
                          roots.Add(New With {.source = source, .kind = kind, .path = folder, .exists = Directory.Exists(folder)})
                      End Sub
        Dim desktop = DesktopRoot()
        addRoot("Desktop", desktop, "skills")
        LoadSkills(items, desktop, "desktop", "Desktop", True, Nothing)
        Dim desktopCommands = DesktopCommandsRoot()
        addRoot("Desktop", desktopCommands, "commands")
        LoadCommands(items, desktopCommands, "desktop", "Desktop", True, Nothing)

        Dim claude = PathsUtil.ClaudeHome()
        If _settings.IncludeClaudeCodeSkills Then
            addRoot("Claude Code", Path.Combine(claude, "skills"), "skills")
            LoadSkills(items, Path.Combine(claude, "skills"), "claude", "Claude Code", True, Nothing)
            addRoot("Claude Code", Path.Combine(claude, "commands"), "commands")
            LoadCommands(items, Path.Combine(claude, "commands"), "claude", "Claude Code", True, Nothing)
        End If

        Dim n = 0
        For Each folder In _settings.SkillFolders
            n += 1
            If Not Directory.Exists(folder) Then
                addRoot("Folder", folder, "skills")
                Continue For
            End If
            Dim projectSkills = Path.Combine(folder, ".claude", "skills")
            Dim skillsDir = If(Directory.Exists(projectSkills), projectSkills, folder)
            Dim label = "Folder: " & Path.GetFileName(folder.TrimEnd("\"c, "/"c))
            addRoot(label, skillsDir, "skills")
            LoadSkills(items, skillsDir, "folder" & n, label, True, Nothing)
            Dim projectCommands = Path.Combine(folder, ".claude", "commands")
            If Directory.Exists(projectCommands) Then
                addRoot(label, projectCommands, "commands")
                LoadCommands(items, projectCommands, "folder" & n, label, True, Nothing)
            End If
        Next

        ' Claude Code plugins that are on (/plugin): those installed with Agentic always, Claude Code's own with includeClaudePlugins.
        Dim cache = Path.Combine(claude, "plugins", "cache")
        If _settings.IncludeClaudePlugins AndAlso Directory.Exists(cache) Then addRoot("Claude Code plugins", cache, "plugins")
        Try
            For Each p In Plugins.Enabled(_settings.EffectiveWorkspaces()).Where(Function(x) _settings.IncludeClaudePlugins OrElse x.Source = "Agentic")
                For Each d In Plugins.Folders(p.Dir, "skills")
                    LoadSkills(items, d, "plugin~" & p.Name, "Plugin: " & p.Name, False, p.Name)
                Next
                For Each d In Plugins.Folders(p.Dir, "commands")
                    LoadCommands(items, d, "plugin~" & p.Name, "Plugin: " & p.Name, False, p.Name)
                Next
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try

        ' One name, one item: the first source wins (desktop, Claude Code, folders, plugins).
        Dim unique = items.GroupBy(Function(s) s.Kind & ":" & s.Name.ToLowerInvariant()).Select(Function(g) g.First()).ToList()
        SyncLock _lock
            _items = unique
            _roots = roots
        End SyncLock
    End Sub


    Private Shared Sub LoadSkills(items As List(Of SkillInfo), root As String, sourceKey As String, sourceLabel As String, writable As Boolean, plugin As String)
        If Not Directory.Exists(root) Then Return
        Try
            For Each dir As String In Directory.EnumerateDirectories(root)
                Dim md = Path.Combine(dir, "SKILL.md")
                If Not File.Exists(md) Then md = Path.Combine(dir, "skill.md")
                If Not File.Exists(md) Then Continue For
                Dim parsed = YamlFrontMatter.Split(PathsUtil.ReadFileSafe(md))
                Dim folder = Path.GetFileName(dir)
                Dim name = Str(parsed.Front, "name", folder)
                If plugin IsNot Nothing Then name = plugin & ":" & name
                Dim s As New SkillInfo With {
                    .Id = sourceKey & "~" & folder, .Kind = "skill", .Name = name, .Description = Str(parsed.Front, "description", ""),
                    .Version = Str(parsed.Front, "version", Nothing), .Source = sourceLabel, .Writable = writable, .Location = dir,
                    .Body = parsed.Body, .ModifiedUtc = File.GetLastWriteTimeUtc(md), .AllowedTools = StrList(parsed.Front, "allowed-tools")}
                s.Files = ListFiles(dir)
                items.Add(s)
            Next
        Catch
        End Try
    End Sub

    Private Shared Sub LoadCommands(items As List(Of SkillInfo), root As String, sourceKey As String, sourceLabel As String, writable As Boolean, plugin As String)
        If Not Directory.Exists(root) Then Return
        Try
            For Each md In Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Take(500)
                Dim rel = Path.GetRelativePath(root, md)
                Dim baseName = Path.ChangeExtension(rel, Nothing).Replace("\"c, ":"c).Replace("/"c, ":"c)
                Dim parsed = YamlFrontMatter.Split(PathsUtil.ReadFileSafe(md))
                Dim description = Str(parsed.Front, "description", Nothing)
                If description Is Nothing Then description = parsed.Body.Split(ControlChars.Lf).Select(Function(l) l.Trim().TrimStart("#"c).Trim()).FirstOrDefault(Function(l) l.Length > 0)
                items.Add(New SkillInfo With {
                    .Id = "cmd~" & sourceKey & "~" & baseName, .Kind = "command", .Name = If(plugin Is Nothing, baseName, plugin & ":" & baseName),
                    .Description = If(description, ""), .ArgumentHint = Str(parsed.Front, "argument-hint", Nothing), .Source = sourceLabel,
                    .Writable = writable, .Location = md, .Body = parsed.Body, .ModifiedUtc = File.GetLastWriteTimeUtc(md),
                    .AllowedTools = StrList(parsed.Front, "allowed-tools")})
            Next
        Catch
        End Try
    End Sub

    Private Shared Function ListFiles(dir As String) As List(Of SkillFileRef)
        Dim files As New List(Of SkillFileRef)
        Try
            Dim opts As New EnumerationOptions With {.RecurseSubdirectories = True, .IgnoreInaccessible = True, .MaxRecursionDepth = 5}
            For Each f In New DirectoryInfo(dir).EnumerateFiles("*", opts)
                Dim rel = Path.GetRelativePath(dir, f.FullName).Replace("\"c, "/"c)
                If rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) OrElse rel.Contains("node_modules/") OrElse rel.StartsWith(".git/") OrElse rel.Contains("__pycache__/") Then Continue For
                files.Add(New SkillFileRef With {.Path = rel, .SizeBytes = f.Length})
                If files.Count >= 300 Then Exit For
            Next
        Catch
        End Try
        Return files.OrderBy(Function(f) f.Path, StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Private Shared Function Str(map As Dictionary(Of String, Object), key As String, fallback As String) As String
        Dim o As Object = Nothing
        If map.TryGetValue(key, o) AndAlso o IsNot Nothing Then
            Dim v = If(TypeOf o Is List(Of String), String.Join(", ", DirectCast(o, List(Of String))), o.ToString())
            If v.Length > 0 Then Return v
        End If
        Return fallback
    End Function

    Private Shared Function StrList(map As Dictionary(Of String, Object), key As String) As List(Of String)
        Dim o As Object = Nothing
        If Not map.TryGetValue(key, o) OrElse o Is Nothing Then Return New List(Of String)
        If TypeOf o Is List(Of String) Then Return DirectCast(o, List(Of String))
        Return o.ToString().Split(","c).Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0).ToList()
    End Function

    ' ------------------------------------------------------------------ queries

    Public Function All() As List(Of SkillInfo)
        SyncLock _lock
            Return _items.OrderBy(Function(s) s.Kind).ThenBy(Function(s) s.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End SyncLock
    End Function

    Public Function Skills() As List(Of SkillInfo)
        Return All().Where(Function(s) s.Kind = "skill").ToList()
    End Function

    Public Function [Get](id As String) As SkillInfo
        SyncLock _lock
            Return _items.FirstOrDefault(Function(s) s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        End SyncLock
    End Function

    ''' <summary>By invocation name ("standup", "/standup", "plugin:skill"); skills before commands.</summary>
    Public Function FindByName(name As String) As SkillInfo
        If String.IsNullOrWhiteSpace(name) Then Return Nothing
        Dim n = name.Trim().TrimStart("/"c)
        SyncLock _lock
            Return _items.Where(Function(s) s.Name.Equals(n, StringComparison.OrdinalIgnoreCase) OrElse s.Id.Equals(n, StringComparison.OrdinalIgnoreCase)).
                OrderBy(Function(s) If(s.Kind = "skill", 0, 1)).FirstOrDefault()
        End SyncLock
    End Function

    ''' <summary>A file inside the skill's folder (relative path; nothing outside the folder).</summary>
    Public Function ReadFile(s As SkillInfo, relative As String) As Byte()
        If s Is Nothing OrElse s.Kind <> "skill" OrElse String.IsNullOrWhiteSpace(relative) Then Return Nothing
        Dim full = Path.GetFullPath(Path.Combine(s.Location, relative.Replace("/"c, "\"c)))
        If Not PathsUtil.IsInside(full, s.Location) OrElse Not File.Exists(full) Then Return Nothing
        Return File.ReadAllBytes(full)
    End Function

    ''' <summary>A slash command's prompt with $ARGUMENTS (and $1, $2 …) filled in.</summary>
    Public Shared Function ExpandCommand(s As SkillInfo, args As String) As String
        Dim text = If(s.Body, "")
        args = If(args, "").Trim()
        Dim parts = args.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
        For i = 9 To 1 Step -1
            text = text.Replace("$" & i, If(i <= parts.Length, parts(i - 1), ""))
        Next
        If text.Contains("$ARGUMENTS") Then Return text.Replace("$ARGUMENTS", args)
        Return If(args.Length > 0, text & vbLf & vbLf & "Arguments: " & args, text)
    End Function

    ' ------------------------------------------------------------------ changes (writable sources only)

    Public Shared Function Slug(name As String) As String
        Dim sb As New StringBuilder()
        For Each c In If(name, "").Trim().ToLowerInvariant()
            If Char.IsLetterOrDigit(c) Then
                sb.Append(c)
            ElseIf sb.Length > 0 AndAlso sb(sb.Length - 1) <> "-"c Then
                sb.Append("-"c)
            End If
        Next
        Dim s = sb.ToString().Trim("-"c)
        Return If(s.Length = 0, "skill", If(s.Length > 60, s.Substring(0, 60).Trim("-"c), s))
    End Function

    Private Shared Function Front(name As String, description As String, extra As IEnumerable(Of KeyValuePair(Of String, String))) As String
        Dim sb As New StringBuilder("---" & vbLf)
        sb.Append("name: ").Append(name).Append(vbLf)
        sb.Append("description: ").Append(Quote(description)).Append(vbLf)
        For Each kv In extra
            sb.Append(kv.Key).Append(": ").Append(Quote(kv.Value)).Append(vbLf)
        Next
        Return sb.Append("---" & vbLf & vbLf).ToString()
    End Function

    Private Shared Function Quote(value As String) As String
        value = If(value, "").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
        Return If(value.Contains(":") OrElse value.Contains("#") OrElse value.StartsWith("'") OrElse value.StartsWith(""""), """" & value.Replace("""", "\""") & """", value)
    End Function

    ''' <summary>Creates a skill (kind "skill") or slash command (kind "command") in the desktop folders.</summary>
    Public Function Create(kind As String, name As String, description As String, body As String) As SkillInfo
        Dim slugName = Slug(name)
        If kind = "command" Then
            Dim file = Path.Combine(DesktopCommandsRoot(), slugName & ".md")
            If IO.File.Exists(file) Then Throw New InvalidOperationException($"A command named {slugName} already exists.")
            PathsUtil.WriteFileSafe(file, Front(slugName, description, {}).Replace("name: " & slugName & vbLf, "") & If(body, "").Trim() & vbLf)
        Else
            Dim dir = Path.Combine(DesktopRoot(), slugName)
            If Directory.Exists(dir) Then Throw New InvalidOperationException($"A skill named {slugName} already exists.")
            Directory.CreateDirectory(dir)
            PathsUtil.WriteFileSafe(Path.Combine(dir, "SKILL.md"), Front(slugName, description, {}) & If(body, "").Trim() & vbLf)
        End If
        Refresh()
        Return FindByName(slugName)
    End Function

    ''' <summary>Rewrites the description and instructions (other front matter such as allowed-tools is kept).</summary>
    Public Function Update(id As String, description As String, body As String) As SkillInfo
        Dim s = [Get](id)
        If s Is Nothing Then Throw New KeyNotFoundException("No such skill.")
        If Not s.Writable Then Throw New InvalidOperationException("Plugin skills are read-only; copy it to the desktop skills to change it.")
        Dim file = If(s.Kind = "skill", Path.Combine(s.Location, "SKILL.md"), s.Location)
        If s.Kind = "skill" AndAlso Not IO.File.Exists(file) Then file = Path.Combine(s.Location, "skill.md")
        Dim existing = YamlFrontMatter.Split(PathsUtil.ReadFileSafe(file)).Front
        Dim extra = existing.Where(Function(kv) Not kv.Key.Equals("name", StringComparison.OrdinalIgnoreCase) AndAlso Not kv.Key.Equals("description", StringComparison.OrdinalIgnoreCase)).
            Select(Function(kv) New KeyValuePair(Of String, String)(kv.Key, If(TypeOf kv.Value Is List(Of String), String.Join(", ", DirectCast(kv.Value, List(Of String))), If(kv.Value?.ToString(), ""))))
        Dim header = Front(If(s.Kind = "skill", Str(existing, "name", Path.GetFileName(s.Location)), ""), description, extra)
        If s.Kind = "command" Then header = header.Replace("name: " & vbLf, "")
        PathsUtil.WriteFileSafe(file, header & If(body, "").Trim() & vbLf)
        Refresh()
        Return [Get](id)
    End Function

    ''' <summary>Copies any skill (e.g. from a plugin) into the desktop skills so it can be changed.</summary>
    Public Function CopyToDesktop(id As String) As SkillInfo
        Dim s = [Get](id)
        If s Is Nothing OrElse s.Kind <> "skill" Then Throw New KeyNotFoundException("No such skill.")
        Dim dest = Path.Combine(DesktopRoot(), Slug(s.Name.Replace(":"c, "-"c)))
        If Directory.Exists(dest) Then Throw New InvalidOperationException("The desktop already has a skill with this name.")
        For Each f In Directory.EnumerateFiles(s.Location, "*", SearchOption.AllDirectories)
            Dim target = Path.Combine(dest, Path.GetRelativePath(s.Location, f))
            Directory.CreateDirectory(Path.GetDirectoryName(target))
            File.Copy(f, target)
        Next
        Refresh()
        Return [Get]("desktop~" & Path.GetFileName(dest))
    End Function

    Public Function Delete(id As String) As Boolean
        Dim s = [Get](id)
        If s Is Nothing OrElse Not s.Source.Equals("Desktop") Then Return False
        If s.Kind = "skill" Then Directory.Delete(s.Location, True) Else File.Delete(s.Location)
        Refresh()
        Return True
    End Function

    ''' <summary>Imports a .zip with a skill (SKILL.md at the top or in one folder) into the desktop skills.</summary>
    Public Function ImportZip(fileName As String, zipBytes As Byte()) As SkillInfo
        Using archive As New ZipArchive(New MemoryStream(zipBytes), ZipArchiveMode.Read)
            Dim skillEntry = archive.Entries.Where(Function(e) e.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)).OrderBy(Function(e) e.FullName.Length).FirstOrDefault()
            If skillEntry Is Nothing Then Throw New InvalidOperationException("The zip has no SKILL.md.")
            Dim prefix = skillEntry.FullName.Substring(0, skillEntry.FullName.Length - skillEntry.Name.Length)
            Dim name As String
            Using reader As New StreamReader(skillEntry.Open())
                name = Str(YamlFrontMatter.Parse(reader.ReadToEnd()), "name", Nothing)
            End Using
            Dim folder = Slug(If(name, If(prefix.TrimEnd("/"c).Length > 0, Path.GetFileName(prefix.TrimEnd("/"c)), Path.GetFileNameWithoutExtension(fileName))))
            Dim dest = Path.Combine(DesktopRoot(), folder)
            If Directory.Exists(dest) Then Throw New InvalidOperationException($"A skill named {folder} already exists.")
            For Each entry In archive.Entries
                If String.IsNullOrEmpty(entry.Name) OrElse Not entry.FullName.StartsWith(prefix, StringComparison.Ordinal) Then Continue For
                Dim target = Path.GetFullPath(Path.Combine(dest, entry.FullName.Substring(prefix.Length).Replace("/"c, "\"c)))
                If Not PathsUtil.IsInside(target, dest) Then Continue For
                Directory.CreateDirectory(Path.GetDirectoryName(target))
                entry.ExtractToFile(target, False)
            Next
            Refresh()
            Return [Get]("desktop~" & folder)
        End Using
    End Function

End Class
