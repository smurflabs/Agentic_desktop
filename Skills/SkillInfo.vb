''' <summary>One file in a skill's folder (path relative to the folder, with "/").</summary>
Public Class SkillFileRef
    Public Property Path As String
    Public Property SizeBytes As Long
End Class

''' <summary>
''' A Claude Code skill (a folder with SKILL.md: YAML front matter with name and description, then instructions; plus
''' optional scripts, references and assets) or a slash command (a .md file whose body is the prompt; $ARGUMENTS is
''' replaced by what follows the command).
''' </summary>
Public Class SkillInfo
    ''' <summary>Unique, URL-safe: "source~folder" (plugins: "plugin~plugin~folder").</summary>
    Public Property Id As String
    Public Property Kind As String = "skill"
    ''' <summary>The name used to invoke it (/name) and by the model.</summary>
    Public Property Name As String
    Public Property Description As String
    Public Property ArgumentHint As String
    Public Property AllowedTools As New List(Of String)
    Public Property Version As String
    ''' <summary>Desktop | Claude Code | Plugin: name | Folder</summary>
    Public Property Source As String
    Public Property Writable As Boolean
    ''' <summary>The skill folder (skills) or the .md file (commands).</summary>
    Public Property Location As String
    Public Property Body As String
    Public Property Files As New List(Of SkillFileRef)
    Public Property ModifiedUtc As DateTime

    Public Function ToJson() As Object
        Return New With {
            .id = Id, .kind = Kind, .name = Name, .description = Description, .argumentHint = ArgumentHint, .version = Version,
            .allowedTools = AllowedTools, .source = Source, .writable = Writable, .location = Location,
            .fileCount = Files.Count, .fileBytes = Files.Sum(Function(f) f.SizeBytes), .modifiedUtc = ModifiedUtc}
    End Function
End Class
