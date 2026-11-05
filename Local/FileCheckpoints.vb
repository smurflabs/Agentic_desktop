Imports System.Collections.Concurrent
Imports System.IO
Imports System.Text.Json

''' <summary>
''' Code checkpoints for Rewind (like Claude Code's): before a tool changes a file for the first time in an answer, the
''' file is saved under the question that started that answer. Rewinding to a question puts back every file changed from
''' then on (deleting files that did not exist). Changes made by shell commands are not tracked.
''' Kept in %AppData%\…\checkpoints\&lt;chat&gt;\ (index.json and the saved copies); chats untouched for 30 days are removed.
''' </summary>
Public NotInheritable Class FileCheckpoints

    Public NotInheritable Class SavedFile
        Public Property Path As String
        Public Property Existed As Boolean
        Public Property Backup As String
    End Class

    Public NotInheritable Class Turn
        Public Property MessageId As String
        Public Property TimeUtc As DateTime
        Public Property Files As New List(Of SavedFile)
    End Class

    ''' <summary>Files larger than this are not saved (and so cannot be put back).</summary>
    Public Const MaxFileBytes As Long = 20L * 1024 * 1024

    Private ReadOnly _root As String
    Private ReadOnly _current As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
    Private ReadOnly _lock As New Object()
    Private _lastCleanup As DateTime = DateTime.MinValue

    Public Sub New(root As String)
        _root = root
        Directory.CreateDirectory(_root)
    End Sub

    ''' <summary>An answer to this question starts (or continues): file changes from now on are filed under it.</summary>
    Public Sub BeginTurn(chatId As String, messageId As String)
        If String.IsNullOrWhiteSpace(chatId) OrElse String.IsNullOrWhiteSpace(messageId) Then Return
        _current(chatId) = messageId
        Cleanup()
    End Sub

    ''' <summary>Saves the file as it is now, if this answer has not changed it yet.</summary>
    Public Sub BeforeChange(chatId As String, path As String)
        If String.IsNullOrWhiteSpace(chatId) OrElse String.IsNullOrWhiteSpace(path) Then Return
        Dim messageId As String = Nothing
        If Not _current.TryGetValue(chatId, messageId) Then Return
        SyncLock _lock
            Dim turns = Load(chatId)
            Dim turn = turns.FirstOrDefault(Function(t) t.MessageId = messageId)
            If turn Is Nothing Then
                turn = New Turn With {.MessageId = messageId, .TimeUtc = DateTime.UtcNow}
                turns.Add(turn)
            End If
            If turn.Files.Any(Function(f) String.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) Then Return
            Dim saved As New SavedFile With {.Path = path, .Existed = File.Exists(path)}
            If saved.Existed Then
                If New FileInfo(path).Length > MaxFileBytes Then Return
                Dim dir = IO.Path.Combine(ChatDir(chatId), "files")
                Directory.CreateDirectory(dir)
                saved.Backup = IO.Path.Combine(dir, Guid.NewGuid().ToString("N"))
                File.Copy(path, saved.Backup, True)
            End If
            turn.Files.Add(saved)
            Save(chatId, turns)
        End SyncLock
    End Sub

    ''' <summary>Each question's changed files: [{messageId, files}].</summary>
    Public Function List(chatId As String) As List(Of Object)
        SyncLock _lock
            Return Load(chatId).Select(Function(t) CObj(New With {.messageId = t.MessageId, .timeUtc = t.TimeUtc, .files = t.Files.Select(Function(f) f.Path).ToList()})).ToList()
        End SyncLock
    End Function

    ''' <summary>
    ''' Puts back every file changed in the answers to these questions, to how it was before the earliest of them (a file
    ''' that did not exist then is deleted). Those checkpoints are then gone (their changes are undone).
    ''' </summary>
    Public Function Restore(chatId As String, messageIds As IEnumerable(Of String)) As (Restored As List(Of String), Failed As List(Of Object))
        Dim ids = New HashSet(Of String)(If(messageIds, Enumerable.Empty(Of String)()), StringComparer.Ordinal)
        Dim restored As New List(Of String)
        Dim failed As New List(Of Object)
        SyncLock _lock
            Dim turns = Load(chatId)
            Dim chosen = turns.Where(Function(t) ids.Contains(t.MessageId)).OrderBy(Function(t) t.TimeUtc).ToList()
            Dim done As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each t In chosen
                For Each f In t.Files
                    If Not done.Add(f.Path) Then Continue For   ' the earliest saved state wins
                    Try
                        If f.Existed Then
                            Directory.CreateDirectory(IO.Path.GetDirectoryName(f.Path))
                            File.Copy(f.Backup, f.Path, True)
                        ElseIf File.Exists(f.Path) Then
                            File.Delete(f.Path)
                        End If
                        restored.Add(f.Path)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        failed.Add(New With {.path = f.Path, .error = ex.Message})
                    End Try
                Next
            Next
            For Each t In chosen
                turns.Remove(t)
                For Each f In t.Files.Where(Function(x) x.Backup IsNot Nothing)
                    Try
                        File.Delete(f.Backup)
                    Catch ex As IOException
                    End Try
                Next
            Next
            Save(chatId, turns)
        End SyncLock
        Return (restored, failed)
    End Function

    Private Function ChatDir(chatId As String) As String
        Dim safe = String.Concat(chatId.Select(Function(c) If(Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c, c, "_"c)))
        Return IO.Path.Combine(_root, safe)
    End Function

    Private Function Load(chatId As String) As List(Of Turn)
        Dim file = IO.Path.Combine(ChatDir(chatId), "index.json")
        If Not IO.File.Exists(file) Then Return New List(Of Turn)
        Try
            Return If(JsonSerializer.Deserialize(Of List(Of Turn))(IO.File.ReadAllText(file)), New List(Of Turn))
        Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
            Return New List(Of Turn)
        End Try
    End Function

    Private Sub Save(chatId As String, turns As List(Of Turn))
        Dim dir = ChatDir(chatId)
        Directory.CreateDirectory(dir)
        IO.File.WriteAllText(IO.Path.Combine(dir, "index.json"), JsonSerializer.Serialize(turns))
    End Sub

    ''' <summary>Chats whose checkpoints were not touched for 30 days are removed (at most once an hour).</summary>
    Private Sub Cleanup()
        If (DateTime.UtcNow - _lastCleanup).TotalHours < 1 Then Return
        _lastCleanup = DateTime.UtcNow
        Try
            For Each d In Directory.GetDirectories(_root)
                If (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d)).TotalDays > 30 Then Directory.Delete(d, True)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub
End Class
