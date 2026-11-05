Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading

' fetch_url: a web address fetched from this computer - it reaches what the Agentic server cannot: localhost (a dev server),
' the LAN and the intranet. A page becomes text; a picture goes to the model as a picture. The chat asks before a site it has
' not opened yet.

Partial Public Class LocalTools

    Private Shared ReadOnly FetchHttp As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = True, .MaxAutomaticRedirections = 5}) With {.Timeout = TimeSpan.FromSeconds(30)}

    Private Shared Function FetchDefinition() As JsonObject
        Return Def("fetch_url", "Fetch a web address from the user's computer: it reaches localhost (a dev server you started), the LAN and the intranet, which the Agentic server cannot. " &
                   "A page comes back as text, JSON as it is, a picture as a picture. GET by default; method and body for an API.",
                   "{""type"":""object"",""properties"":{""url"":{""type"":""string""},""method"":{""type"":""string"",""enum"":[""GET"",""HEAD"",""POST"",""PUT"",""DELETE""]}," &
                   """body"":{""type"":""string"",""description"":""Request body (JSON or text) for POST/PUT""},""headers"":{""type"":""object"",""additionalProperties"":{""type"":""string""}}},""required"":[""url""]}")
    End Function

    ''' <summary>A page's text: no scripts, styles or tags; entities decoded; blank runs folded.</summary>
    Friend Shared Function HtmlToText(html As String) As String
        Dim t = Regex.Replace(html, "<(script|style|noscript|svg)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase)
        t = Regex.Replace(t, "<(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", vbLf, RegexOptions.IgnoreCase)
        t = Regex.Replace(t, "<[^>]+>", " ")
        t = WebUtility.HtmlDecode(t)
        t = Regex.Replace(t, "[ \t\r\f]+", " ")
        t = Regex.Replace(t, "\n\s*\n+", vbLf & vbLf)
        Return t.Trim()
    End Function

    Private Async Function FetchUrlAsync(args As JsonElement, ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String, Image As ToolImage))
        Dim url = Str(args, "url", "").Trim()
        Dim u As Uri = Nothing
        If Not Uri.TryCreate(url, UriKind.Absolute, u) OrElse (u.Scheme <> "http" AndAlso u.Scheme <> "https") Then Return (False, "Give an http(s) URL.", Nothing)
        Dim method = Str(args, "method", "GET").ToUpperInvariant()
        Using req As New HttpRequestMessage(New HttpMethod(method), u)
            req.Headers.TryAddWithoutValidation("User-Agent", "AgenticDesktop/1.0")
            Dim hdrs As JsonElement
            If args.TryGetProperty("headers", hdrs) AndAlso hdrs.ValueKind = JsonValueKind.Object Then
                For Each p In hdrs.EnumerateObject()
                    If p.Value.ValueKind = JsonValueKind.String AndAlso Not req.Headers.TryAddWithoutValidation(p.Name, p.Value.GetString()) Then
                        req.Content = If(req.Content, New StringContent(""))
                        req.Content.Headers.TryAddWithoutValidation(p.Name, p.Value.GetString())
                    End If
                Next
            End If
            Dim body = Str(args, "body", Nothing)
            If body IsNot Nothing AndAlso method <> "GET" AndAlso method <> "HEAD" Then
                req.Content = New StringContent(body, System.Text.Encoding.UTF8, If(body.TrimStart().StartsWith("{") OrElse body.TrimStart().StartsWith("["), "application/json", "text/plain"))
            End If
            Using res = Await FetchHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                Dim type = If(res.Content.Headers.ContentType?.MediaType, "")
                Dim head = $"HTTP {CInt(res.StatusCode)} {res.ReasonPhrase} · {If(type.Length = 0, "no content type", type)} · {res.RequestMessage.RequestUri}"
                Dim bytes = Await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(False)
                If bytes.Length > 8 * 1024 * 1024 Then Return (res.IsSuccessStatusCode, head & vbLf & $"({SizeText(bytes.Length)}: too large to show)", Nothing)
                If {"image/png", "image/jpeg", "image/webp", "image/gif"}.Contains(type) AndAlso bytes.Length > 0 Then
                    Return (res.IsSuccessStatusCode, head & vbLf & "The picture follows as an image.", New ToolImage With {.MediaType = type, .Data = bytes, .Name = IO.Path.GetFileName(u.AbsolutePath)})
                End If
                If bytes.Take(8000).Contains(CByte(0)) Then Return (res.IsSuccessStatusCode, head & vbLf & $"(binary, {SizeText(bytes.Length)})", Nothing)
                Dim page = System.Text.Encoding.UTF8.GetString(bytes)
                If type.Contains("html") Then page = HtmlToText(page)
                Return (res.IsSuccessStatusCode, head & vbLf & vbLf & Clip(page), Nothing)
            End Using
        End Using
    End Function

End Class
