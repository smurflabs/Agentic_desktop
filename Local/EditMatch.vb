Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.RegularExpressions

''' <summary>
''' edit_file's matching: old_string replaced by new_string exactly once (it must be unique) or everywhere. Forgiving where it is
''' safe - LF edits on CRLF files, line numbers copied from read_file, spaces at line ends - and when the text is not there the
''' error shows the closest lines, so the model's next try can be right without reading the file again.
''' </summary>
Public Module EditMatch

    Public Structure Outcome
        Public Ok As Boolean
        ''' <summary>The new file text (Nothing when the edit failed).</summary>
        Public Text As String
        Public Replaced As Integer
        Public Message As String
    End Structure

    ''' <summary>A line-number prefix copied from read_file's output ("    12&lt;tab&gt;", "12: ", "12 | ").</summary>
    Private ReadOnly LinePrefix As New Regex("^\s*\d+(\t|: |\s?[|│] )")

    Private Function Fail(message As String) As Outcome
        Return New Outcome With {.Ok = False, .Message = message}
    End Function

    Private Function Count(source As String, part As String) As Integer
        If part.Length = 0 Then Return 0
        Dim n = 0, at = source.IndexOf(part, StringComparison.Ordinal)
        While at >= 0
            n += 1
            at = source.IndexOf(part, at + part.Length, StringComparison.Ordinal)
        End While
        Return n
    End Function

    Public Function Apply(text As String, oldText As String, newText As String, replaceAll As Boolean) As Outcome
        If String.IsNullOrEmpty(oldText) Then Return Fail("old_string is empty: use write_file to create or replace a whole file.")
        ' Files with CRLF: the model usually writes LF, so everything is matched as LF; the file keeps its line endings (each
        ' unchanged line its own, so a file with mixed endings changes only where it was edited).
        Dim crlf = text.Contains(vbCrLf)
        Dim source = If(crlf, text.Replace(vbCrLf, vbLf), text)
        Dim fromText = oldText.Replace(vbCrLf, vbLf), toText = If(newText, "").Replace(vbCrLf, vbLf)
        Dim finish = Function(result As String, replaced As Integer, said As String) New Outcome With {
            .Ok = True, .Text = If(crlf, LineEndings.Restore(text, result), result), .Replaced = replaced, .Message = said}
        If fromText = toText Then
            If source.Contains(fromText) Then Return New Outcome With {.Ok = True, .Text = text, .Replaced = 0, .Message = "No change needed: the file already contains exactly this text."}
            Return Fail("old_string and new_string are the same, and the file does not contain that text.")
        End If
        Dim n = Count(source, fromText)
        Dim note As String = Nothing
        If n = 0 Then
            Dim stripped = WithoutLineNumbers(fromText, toText)
            If stripped.HasValue AndAlso Count(source, stripped.Value.Old) > 0 Then
                fromText = stripped.Value.Old : toText = stripped.Value.[New]
                n = Count(source, fromText)
                note = "The line numbers copied from read_file were left out of old_string."
            End If
        End If
        If n = 0 AndAlso Not replaceAll Then
            Dim loose = LooseLineMatches(source, fromText)
            If loose.Count = 1 Then
                Dim lines = source.Split(ControlChars.Lf).ToList()
                lines.RemoveRange(loose(0).Start, loose(0).Length)
                lines.InsertRange(loose(0).Start, toText.TrimEnd(ControlChars.Lf).Split(ControlChars.Lf))
                Return finish(String.Join(vbLf, lines), 1, "Matched ignoring spaces at line ends.")
            End If
            If loose.Count > 1 Then Return Fail($"old_string matches {loose.Count} places (ignoring spaces at line ends): give more surrounding text so it is unique.")
        End If
        If n = 0 Then
            Dim near = ClosestText(source, fromText)
            Return Fail("old_string was not found in the file (it must match exactly, including indentation)." &
                        If(near.Length > 0, " The closest text in the file:" & vbLf & near & vbLf & "Copy the exact text from it (without the line numbers) and try again.", " Read the file again first."))
        End If
        If n > 1 AndAlso Not replaceAll Then Return Fail($"old_string occurs {n} times: give more surrounding text so it is unique, or set replace_all.")
        Dim updated As String
        If replaceAll Then
            updated = source.Replace(fromText, toText)
        Else
            Dim at = source.IndexOf(fromText, StringComparison.Ordinal)
            updated = source.Substring(0, at) & toText & source.Substring(at + fromText.Length)
        End If
        Return finish(updated, If(replaceAll, n, 1), note)
    End Function

    ''' <summary>old/new without line numbers, when every non-blank line of old starts with one (copied from read_file).</summary>
    Private Function WithoutLineNumbers(oldText As String, newText As String) As (Old As String, [New] As String)?
        Dim oldLines = oldText.Split(ControlChars.Lf).Where(Function(l) l.Trim().Length > 0).ToList()
        If oldLines.Count = 0 OrElse Not oldLines.All(Function(l) LinePrefix.IsMatch(l)) Then Return Nothing
        Dim strip = Function(s As String) String.Join(vbLf, s.Split(ControlChars.Lf).Select(Function(l) LinePrefix.Replace(l, "", 1)))
        Dim newLines = newText.Split(ControlChars.Lf).Where(Function(l) l.Trim().Length > 0).ToList()
        Return (strip(oldText), If(newLines.Count > 0 AndAlso newLines.All(Function(l) LinePrefix.IsMatch(l)), strip(newText), newText))
    End Function

    ''' <summary>Where old's lines appear in the file when spaces at line ends are ignored (start line, line count).</summary>
    Private Function LooseLineMatches(source As String, oldText As String) As List(Of (Start As Integer, Length As Integer))
        Dim hits As New List(Of (Start As Integer, Length As Integer))
        Dim lines = source.Split(ControlChars.Lf)
        Dim want = oldText.TrimEnd(ControlChars.Lf).Split(ControlChars.Lf).Select(Function(l) l.TrimEnd()).ToArray()
        If Not want.Any(Function(l) l.Trim().Length > 0) Then Return hits
        For i = 0 To lines.Length - want.Length
            Dim j = 0
            While j < want.Length AndAlso lines(i + j).TrimEnd() = want(j)
                j += 1
            End While
            If j = want.Length Then hits.Add((i, want.Length))
        Next
        Return hits
    End Function

    ''' <summary>The file's lines nearest to old's first line (the one sharing most words with it), numbered like read_file.</summary>
    Public Function ClosestText(source As String, oldText As String) As String
        Dim first = oldText.Split(ControlChars.Lf).FirstOrDefault(Function(l) l.Trim().Length > 0)
        If first Is Nothing Then Return ""
        Dim words = New HashSet(Of String)(Regex.Split(first, "\W+").Where(Function(w) w.Length > 1), StringComparer.Ordinal)
        If words.Count = 0 Then Return ""
        Dim lines = source.Split(ControlChars.Lf)
        Dim best = -1, bestScore = 0
        For i = 0 To lines.Length - 1
            Dim score = Regex.Split(lines(i), "\W+").Count(Function(w) words.Contains(w))
            If score > bestScore Then best = i : bestScore = score
        Next
        If best < 0 OrElse bestScore < Math.Min(2, words.Count) Then Return ""
        Dim span = Math.Max(1, oldText.Split(ControlChars.Lf).Length)
        Dim fromLine = Math.Max(0, best - 2), toLine = Math.Min(lines.Length, best + span + 2)
        Dim sb As New StringBuilder()
        For k = fromLine To toLine - 1
            If sb.Length > 0 Then sb.Append(vbLf)
            sb.Append((k + 1).ToString().PadLeft(6)).Append(ControlChars.Tab).Append(lines(k))
        Next
        Return sb.ToString()
    End Function

End Module
