Imports System.Collections.Concurrent
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

''' <summary>A text file's content with its encoding and byte order mark, so a rewrite keeps them (UTF-16 and ANSI files too).</summary>
Public NotInheritable Class TextFileData
    ''' <summary>The text as it is in the file, line endings included.</summary>
    Public Property Text As String = ""
    Public Property Enc As Encoding = New UTF8Encoding(False)
    Public Property Bom As Byte() = Array.Empty(Of Byte)()
    Public Property Exists As Boolean

    Public Shared Function Load(file As String) As TextFileData
        Dim t As New TextFileData()
        If Not IO.File.Exists(file) Then Return t
        t.Exists = True
        Dim bytes = IO.File.ReadAllBytes(file)
        Dim start = 0
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then
            start = 3 : t.Bom = {&HEF, &HBB, &HBF}
        ElseIf bytes.Length >= 2 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HFE Then
            start = 2 : t.Enc = New UnicodeEncoding(False, False) : t.Bom = {&HFF, &HFE}
        ElseIf bytes.Length >= 2 AndAlso bytes(0) = &HFE AndAlso bytes(1) = &HFF Then
            start = 2 : t.Enc = New UnicodeEncoding(True, False) : t.Bom = {&HFE, &HFF}
        End If
        Try
            t.Text = If(t.Bom.Length = 2, t.Enc, New UTF8Encoding(False, True)).GetString(bytes, start, bytes.Length - start)
        Catch ex As DecoderFallbackException
            ' Not UTF-8: an older ANSI file. Latin-1 maps every byte, so writing it back changes only what was edited.
            t.Enc = Encoding.Latin1
            t.Text = Encoding.Latin1.GetString(bytes, start, bytes.Length - start)
        End Try
        Return t
    End Function

    Public Sub Save(file As String, content As String)
        Dim dir = Path.GetDirectoryName(file)
        If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
        Dim data = Enc.GetBytes(content)
        If Bom.Length > 0 Then data = Bom.Concat(data).ToArray()
        IO.File.WriteAllBytes(file, data)
    End Sub
End Class

''' <summary>
''' Line endings for a changed file: lines the change did not touch (the same lines at the start and at the end) keep the
''' ending they had, lines replacing as many old ones keep theirs one for one, and other new lines take the file's usual
''' one. A file with mixed endings then changes only where it was edited, instead of on every line.
''' </summary>
Public Module LineEndings

    Public Function Restore(originalRaw As String, newText As String) As String
        Dim original = If(originalRaw, "")
        If Not original.Contains(vbCr) Then Return If(newText, "").Replace(vbCrLf, vbLf)
        Dim crlf = 0, lf = 0
        Dim oldLines As New List(Of String), oldEnds As New List(Of String)
        Dim start = 0
        For i = 0 To original.Length - 1
            If original(i) <> ControlChars.Lf Then Continue For
            Dim isCrlf = i > start AndAlso original(i - 1) = ControlChars.Cr
            oldLines.Add(original.Substring(start, i - start - If(isCrlf, 1, 0)))
            oldEnds.Add(If(isCrlf, vbCrLf, vbLf))
            If isCrlf Then crlf += 1 Else lf += 1
            start = i + 1
        Next
        Dim usual = If(crlf > lf, vbCrLf, vbLf)
        Dim parts = If(newText, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
        Dim count = parts.Length - 1
        Dim ends(Math.Max(0, count) - 1) As String
        Dim p = 0
        While p < count AndAlso p < oldLines.Count AndAlso parts(p) = oldLines(p)
            ends(p) = oldEnds(p)
            p += 1
        End While
        Dim s = 0
        While s < count - p AndAlso s < oldLines.Count - p AndAlso parts(count - 1 - s) = oldLines(oldLines.Count - 1 - s)
            ends(count - 1 - s) = oldEnds(oldLines.Count - 1 - s)
            s += 1
        End While
        Dim sameCount = count - p - s = oldLines.Count - p - s
        Dim sb As New StringBuilder()
        For i = 0 To count - 1
            sb.Append(parts(i)).Append(If(ends(i), If(sameCount, oldEnds(i), usual)))
        Next
        Return sb.Append(parts(count)).ToString()
    End Function

End Module

''' <summary>What the hooks of one event decided (Claude Code's exit codes and JSON answers).</summary>
Public NotInheritable Class HookResult
    ''' <summary>Exit code 2, {"decision": "block"} or permissionDecision "deny": the action does not happen.</summary>
    Public Property Blocked As Boolean
    ''' <summary>Why it was blocked, or what failed hooks said.</summary>
    Public Property Message As String = ""
    ''' <summary>What the hooks that succeeded printed (plain text; UserPromptSubmit and SessionStart hand it to the model).</summary>
    Public Property Output As String = ""
    ''' <summary>hookSpecificOutput.additionalContext: text for the model.</summary>
    Public Property Context As String
    ''' <summary>allow | deny | ask (PreToolUse permissionDecision, PermissionRequest decision.behavior).</summary>
    Public Property PermissionDecision As String
    ''' <summary>The tool's arguments rewritten by a hook (updatedInput).</summary>
    Public Property UpdatedInput As JsonNode
    ''' <summary>{"continue": false}.</summary>
    Public Property StopAll As Boolean
    Public Property SystemMessage As String
End Class

''' <summary>A picture a tool returns for the model (read_file of an image).</summary>
Public NotInheritable Class ToolImage
    Public Property MediaType As String
    Public Property Data As Byte()
    Public Property Name As String
End Class

' Claude Code's rules for file tools: a file is read before it is changed, and not changed when it changed since it was read.
Partial Public Class LocalTools

    ''' <summary>What each chat read: chat|path → a fingerprint of the file as it was read.</summary>
    Private ReadOnly _reads As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
    ''' <summary>The chat key for files the user attached with @path (they count as read in every chat).</summary>
    Private Const MentionedKey As String = "@"

    Private Shared Function ReadKey(chatId As String, file As String) As String
        Dim full = Path.GetFullPath(file)
        Return If(chatId, "") & "|" & If(OperatingSystem.IsWindows() OrElse OperatingSystem.IsMacOS(), full.ToLowerInvariant(), full)
    End Function

    Private Shared Function Fingerprint(file As String) As String
        Try
            Using fs = IO.File.OpenRead(file)
                Return Convert.ToHexString(SHA256.HashData(fs))
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return Nothing
        End Try
    End Function

    ''' <summary>The chat has seen this file as it is now (read_file, or its own change).</summary>
    Friend Sub NoteRead(chatId As String, file As String)
        Dim f = Fingerprint(file)
        If f IsNot Nothing Then _reads(ReadKey(chatId, file)) = f
    End Sub

    ''' <summary>Why the chat may not change an existing file yet (Nothing: it may): not read in this chat, or changed since.</summary>
    Friend Function StaleReason(chatId As String, file As String) As String
        If Not IO.File.Exists(file) Then Return Nothing
        Dim seen As String = Nothing
        If Not _reads.TryGetValue(ReadKey(chatId, file), seen) AndAlso Not _reads.TryGetValue(ReadKey(MentionedKey, file), seen) Then
            Return $"{file} has not been read in this chat yet. Read it with read_file first, then make the change."
        End If
        If seen <> Fingerprint(file) Then
            Return $"{file} changed since you read it (the user, a formatter or a command changed it). Read it again with read_file, then make the change."
        End If
        Return Nothing
    End Function

    Private Shared ReadOnly ImageTypes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".webp", "image/webp"}}

    ''' <summary>A picture file for the model (PNG, JPEG, WebP up to 8 MB), or Nothing.</summary>
    Friend Shared Function ReadImage(file As String) As ToolImage
        Dim mime As String = Nothing
        If Not ImageTypes.TryGetValue(Path.GetExtension(file), mime) OrElse Not IO.File.Exists(file) Then Return Nothing
        Dim size = New FileInfo(file).Length
        If size = 0 OrElse size > 8L * 1024 * 1024 Then Return Nothing
        Return New ToolImage With {.MediaType = mime, .Data = IO.File.ReadAllBytes(file), .Name = Path.GetFileName(file)}
    End Function

    ''' <summary>True for content that looks binary (a NUL byte in the first 8 KB, not UTF-16).</summary>
    Friend Shared Function LooksBinary(file As String) As Boolean
        Try
            Using fs = IO.File.OpenRead(file)
                Dim buf(8191) As Byte
                Dim n = fs.Read(buf, 0, buf.Length)
                If n >= 2 AndAlso ((buf(0) = &HFF AndAlso buf(1) = &HFE) OrElse (buf(0) = &HFE AndAlso buf(1) = &HFF)) Then Return False
                For i = 0 To n - 1
                    If buf(i) = 0 Then Return True
                Next
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Return False
    End Function

    ''' <summary>The text of a file on this computer, for the approval card's diff (Nothing when it is not a text file in the workspace).</summary>
    Public Function FileTextForApproval(path As String) As String
        Try
            Dim full = Resolve(path)
            If Not IO.File.Exists(full) OrElse New FileInfo(full).Length > 2_000_000 OrElse LooksBinary(full) Then Return Nothing
            Return TextFileData.Load(full).Text
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            Return Nothing
        End Try
    End Function

    ' ------------------------------------------------------------------ the shell's folder carries over (cd), like Claude Code's Bash

    ''' <summary>Where each chat's commands run next (after a cd); missing: the first workspace folder.</summary>
    Private ReadOnly _cwds As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
    Private Const CwdMarker As String = "__AGENTIC_CWD__="
    Private Shared ReadOnly CwdLine As New Regex("(?m)^" & Regex.Escape(CwdMarker) & ".*(\r?\n)?")

    ''' <summary>The folder a command of this chat starts in.</summary>
    Private Function CommandFolder(chatId As String, cwdArg As String) As String
        If Not String.IsNullOrWhiteSpace(cwdArg) Then Return Resolve(cwdArg)
        Dim kept As String = Nothing
        If _cwds.TryGetValue(If(chatId, ""), kept) AndAlso Directory.Exists(kept) AndAlso _settings.EffectiveWorkspaces().Any(Function(r) PathsUtil.IsInside(kept, r)) Then Return kept
        Return _settings.EffectiveWorkspaces()(0)
    End Function

    ''' <summary>The command, then a line with the folder it ended in (PowerShell and bash/sh; cmd keeps the old way).</summary>
    Private Function WithCwdMarker(command As String) As String
        If OperatingSystem.IsWindows() Then
            If _settings.Shell = "cmd" Then Return command
            Return "$global:LASTEXITCODE = $null" & vbLf & command & vbLf &
                   "$__agentic_ok = $?; $__agentic_ec = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } elseif ($__agentic_ok) { 0 } else { 1 }" & vbLf &
                   "[Console]::Out.WriteLine(''); [Console]::Out.WriteLine('" & CwdMarker & "' + (Get-Location).ProviderPath); exit $__agentic_ec"
        End If
        Return command & vbLf & "__agentic_ec=$?" & vbLf & "printf '\n" & CwdMarker & "%s\n' ""$(pwd)""" & vbLf & "exit $__agentic_ec"
    End Function

    ''' <summary>The output without the folder line, and the folder (Nothing when the command did not get to print it).</summary>
    Friend Shared Function SplitCwd(output As String) As (Text As String, Folder As String)
        Dim m = Regex.Match(If(output, ""), "(?:\r?\n)?" & Regex.Escape(CwdMarker) & "([^\r\n]*)\s*$", RegexOptions.RightToLeft)
        If Not m.Success Then Return (StripCwd(output), Nothing)
        Return (StripCwd(output.Remove(m.Index, m.Length)), m.Groups(1).Value.Trim())
    End Function

    ''' <summary>Output without folder lines (what a background command printed at its end).</summary>
    Friend Shared Function StripCwd(text As String) As String
        Return If(text Is Nothing OrElse Not text.Contains(CwdMarker), text, CwdLine.Replace(text, ""))
    End Function

    ''' <summary>After a command: the chat's next commands start where it ended, if that is in a workspace folder; a note otherwise.</summary>
    Private Function KeepCwd(chatId As String, started As String, folder As String) As String
        If String.IsNullOrWhiteSpace(folder) OrElse Not Directory.Exists(folder) Then Return Nothing
        Dim full = Path.GetFullPath(folder)
        If String.Equals(full.TrimEnd(Path.DirectorySeparatorChar), started.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) Then Return Nothing
        If _settings.EffectiveWorkspaces().Any(Function(r) PathsUtil.IsInside(full, r)) Then
            _cwds(If(chatId, "")) = full
            Return Nothing
        End If
        Return "Shell cwd was reset to " & started
    End Function

    ' ------------------------------------------------------------------ hooks: Claude Code's JSON answers

    ''' <summary>Reads one hook's stdout: a JSON answer (decision, permissionDecision, updatedInput, additionalContext…) or plain text.</summary>
    Private Shared Sub ReadHookAnswer(stdout As String, result As HookResult, printed As List(Of String))
        Dim text = If(stdout, "").Trim()
        If Not text.StartsWith("{") Then
            If text.Length > 0 Then printed.Add(If(text.Length > 10000, text.Substring(0, 10000), text))
            Return
        End If
        Dim answer As JsonObject = Nothing
        Try
            answer = TryCast(JsonNode.Parse(text), JsonObject)
        Catch ex As JsonException
        End Try
        If answer Is Nothing Then
            printed.Add(If(text.Length > 10000, text.Substring(0, 10000), text))
            Return
        End If
        Dim str = Function(o As JsonObject, key As String) As String
                      Dim v = TryCast(o?(key), JsonValue)
                      Dim s As String = Nothing
                      Return If(v IsNot Nothing AndAlso v.TryGetValue(s), s, Nothing)
                  End Function
        Dim cont = TryCast(answer("continue"), JsonValue)
        Dim goOn As Boolean
        If cont IsNot Nothing AndAlso cont.TryGetValue(goOn) AndAlso Not goOn Then
            result.StopAll = True
            result.Blocked = True
            result.Message = If(str(answer, "stopReason"), "Stopped by a hook.")
        End If
        Select Case If(str(answer, "decision"), "").ToLowerInvariant()
            Case "block"
                result.Blocked = True
                result.Message = If(str(answer, "reason"), "Blocked by a hook.")
            Case "approve"
                result.PermissionDecision = "allow"
        End Select
        Dim specific = TryCast(answer("hookSpecificOutput"), JsonObject)
        If specific IsNot Nothing Then
            Dim pd = str(specific, "permissionDecision")
            If pd IsNot Nothing Then
                result.PermissionDecision = pd.ToLowerInvariant()
                If result.PermissionDecision = "deny" Then
                    result.Blocked = True
                    result.Message = If(str(specific, "permissionDecisionReason"), "Denied by a hook.")
                End If
            End If
            ' PermissionRequest: {"decision": {"behavior": "allow" | "deny", "message", "updatedInput"}}.
            Dim dec = TryCast(specific("decision"), JsonObject)
            If dec IsNot Nothing Then
                Dim behavior = If(str(dec, "behavior"), "").ToLowerInvariant()
                If behavior = "allow" OrElse behavior = "deny" Then result.PermissionDecision = behavior
                If behavior = "deny" Then result.Message = If(str(dec, "message"), "Denied by a hook.")
                If TypeOf dec("updatedInput") Is JsonObject Then result.UpdatedInput = dec("updatedInput").DeepClone()
            End If
            If TypeOf specific("updatedInput") Is JsonObject Then result.UpdatedInput = specific("updatedInput").DeepClone()
            Dim extra = str(specific, "additionalContext")
            If Not String.IsNullOrWhiteSpace(extra) Then result.Context = If(result.Context Is Nothing, extra, result.Context & vbLf & extra)
        End If
        Dim sys = str(answer, "systemMessage")
        If sys IsNot Nothing Then result.SystemMessage = sys
    End Sub

End Class
