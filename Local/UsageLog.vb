Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes

''' <summary>
''' Token usage in Claude Code's log format, so `npx ccusage` counts Agentic answers too: one JSON line per answer in
''' &lt;claude dir&gt;/projects/agentic-desktop-&lt;workspace&gt;/&lt;chat id&gt;.jsonl. The claude dir is the first of CLAUDE_CONFIG_DIR when set;
''' otherwise ~/.config/claude when ~/.claude exists (ccusage reads both, and Claude Code's own /resume list stays clean),
''' else ~/.claude. AGENTIC_CCUSAGE_DIR overrides it. Switched off with "Usage for ccusage" in the settings.
''' </summary>
Public NotInheritable Class UsageLog

    Private Sub New()
    End Sub

    Private Shared ReadOnly WriteLock As New Object()

    ''' <summary>x.y.z only: ccusage skips lines whose version is not a plain version number.</summary>
    Public Shared Function PlainVersion(v As String) As String
        Dim m = Text.RegularExpressions.Regex.Match(If(v, ""), "\d+\.\d+\.\d+")
        Return If(m.Success, m.Value, "0.0.0")
    End Function

    Public Shared Function ClaudeDataDir() As String
        Dim custom = Environment.GetEnvironmentVariable("AGENTIC_CCUSAGE_DIR")
        If Not String.IsNullOrWhiteSpace(custom) Then Return custom.Trim()
        Dim fromEnv = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        If Not String.IsNullOrWhiteSpace(fromEnv) Then
            Dim first = fromEnv.Split(","c).Select(Function(p) p.Trim()).FirstOrDefault(Function(p) p.Length > 0)
            If first IsNot Nothing Then Return first
        End If
        Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        Return If(Directory.Exists(Path.Combine(home, ".claude")), Path.Combine(home, ".config", "claude"), Path.Combine(home, ".claude"))
    End Function

    ''' <summary>A folder name for a path, like Claude Code's: C:\Users\me\app becomes C--Users-me-app.</summary>
    Public Shared Function ProjectKey(dir As String) As String
        Dim sb As New StringBuilder()
        For Each c In Path.GetFullPath(dir).TrimEnd("\"c, "/"c)
            sb.Append(If(Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "_"c, c, "-"c))
        Next
        Dim key = sb.ToString()
        Return If(key.Length > 120, key.Substring(key.Length - 120), key)
    End Function

    ''' <summary>Appends one answer's usage. <paramref name="message"/> is the answer from the server's "done" event.</summary>
    Public Shared Function Record(app As String, version As String, chatId As String, cwd As String, message As JsonElement, cachedTokens As Long) As Boolean
        Try
            If message.ValueKind <> JsonValueKind.Object OrElse String.IsNullOrWhiteSpace(chatId) Then Return False
            Dim prompt = LongOf(message, "promptTokens"), completion = LongOf(message, "completionTokens")
            If prompt <= 0 AndAlso completion <= 0 Then Return False
            Dim id = LocalTools.Str(message, "id", "")
            If id.Length = 0 Then id = Guid.NewGuid().ToString("N")
            Dim model = LocalTools.Str(message, "routedModel", "")
            If model.Length = 0 Then model = LocalTools.Str(message, "model", "agentic")
            Dim cached = Math.Clamp(cachedTokens, 0L, prompt)
            Dim entry As New JsonObject From {
                {"type", "assistant"}, {"timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}, {"sessionId", chatId},
                {"cwd", cwd}, {"version", PlainVersion(version)}, {"requestId", id}, {"uuid", id}, {"isSidechain", False},
                {"message", New JsonObject From {
                    {"id", id}, {"type", "message"}, {"role", "assistant"}, {"model", model},
                    {"usage", New JsonObject From {{"input_tokens", prompt - cached}, {"output_tokens", completion},
                                                   {"cache_creation_input_tokens", 0}, {"cache_read_input_tokens", cached}}}}}}
            Dim cost As JsonElement
            If message.TryGetProperty("costMicros", cost) AndAlso cost.ValueKind = JsonValueKind.Number Then entry("costUSD") = cost.GetInt64() / 1000000.0
            Dim dir = Path.Combine(ClaudeDataDir(), "projects", $"agentic-{app}-" & ProjectKey(If(String.IsNullOrWhiteSpace(cwd), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cwd)))
            Dim safeId = String.Concat(chatId.Where(Function(c) Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c))
            SyncLock WriteLock
                Directory.CreateDirectory(dir)
                File.AppendAllText(Path.Combine(dir, safeId & ".jsonl"), entry.ToJsonString() & vbLf, New UTF8Encoding(False))
            End SyncLock
            Return True
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException
            Return False
        End Try
    End Function

    Private Shared Function LongOf(e As JsonElement, name As String) As Long
        Dim v As JsonElement
        Dim n As Long
        If e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt64(n) Then Return n
        Return 0
    End Function

End Class
