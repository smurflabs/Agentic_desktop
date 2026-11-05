' Forwards everything that is not a desktop path to the Agentic server with the API key added here (the page never
' sees it). Streams in both directions so live updates, chat answers (server-sent events) and videos arrive as they happen.
' Web app pages get the desktop layer (desktop.css, desktop.js) added to their <head>.

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Threading

Namespace Http

    Public Class ApiProxy

        Private ReadOnly _settings As AppSettings
        Private ReadOnly _http As HttpClient
        ' Accept-Encoding is not passed on: the browser accepts zstd, which a proxy such as Cloudflare then sends, and .NET
        ' cannot decompress it (pages could not get the desktop layer and failed with ERR_CONTENT_DECODING_FAILED). The
        ' HttpClient asks for gzip, deflate and br itself and decompresses them.
        Private Shared ReadOnly SkipRequestHeaders As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Content-Type", "Cookie", "Origin", "Referer",
            "Authorization", "X-Api-Key", "X-Desktop-Token", "Upgrade", "Proxy-Connection", "TE", "Expect", "Accept-Encoding"}
        ' HTTPS-only headers from the server's front (HSTS, HTTP/3, error reporting) mean nothing for the app's local http page.
        Private Shared ReadOnly SkipResponseHeaders As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "Transfer-Encoding", "Connection", "Keep-Alive", "WWW-Authenticate", "Set-Cookie", "Content-Length", "Server", "Date",
            "Alt-Svc", "Strict-Transport-Security", "Nel", "Report-To"}

        ''' <summary>Per-launch secret the desktop layer sends with /api/local requests; put into every web app page.</summary>
        Public Property DesktopToken As String

        Public Sub New(settings As AppSettings)
            _settings = settings
            _http = New HttpClient(New SocketsHttpHandler With {
                .AutomaticDecompression = DecompressionMethods.All, .ConnectTimeout = TimeSpan.FromSeconds(8),
                .UseCookies = False, .AllowAutoRedirect = False}) With {.Timeout = Timeout.InfiniteTimeSpan}
        End Sub

        Public Async Function ForwardAsync(request As RequestContext) As Task
            Dim incoming = request.ListenerContext.Request
            Dim outgoing = request.ListenerContext.Response
            Dim isDocument = request.IsDocument()
            Dim isSpec = request.RelativePath.Equals("api/openapi.json", StringComparison.OrdinalIgnoreCase)
            Dim target = _settings.NormalizedBaseUrl() & incoming.Url.PathAndQuery

            Using upstream As New HttpRequestMessage(New HttpMethod(incoming.HttpMethod), target)
                For Each name In incoming.Headers.AllKeys
                    If name Is Nothing OrElse SkipRequestHeaders.Contains(name) Then Continue For
                    ' Pages are always fetched fresh: they carry this launch's desktop token.
                    If isDocument AndAlso (name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) OrElse name.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase)) Then Continue For
                    upstream.Headers.TryAddWithoutValidation(name, incoming.Headers(name))
                Next
                If _settings.HasKey() Then upstream.Headers.TryAddWithoutValidation("Authorization", "Bearer " & _settings.ApiKey)
                For Each kv In _settings.ExtraHeaders
                    upstream.Headers.Remove(kv.Key)
                    upstream.Headers.TryAddWithoutValidation(kv.Key, kv.Value)
                Next
                If incoming.HasEntityBody Then
                    Using buffer As New IO.MemoryStream()
                        Await incoming.InputStream.CopyToAsync(buffer, 81920, request.Cancellation).ConfigureAwait(False)
                        upstream.Content = New ByteArrayContent(buffer.ToArray())
                    End Using
                    If incoming.ContentType IsNot Nothing Then upstream.Content.Headers.TryAddWithoutValidation("Content-Type", incoming.ContentType)
                End If

                Using response = Await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, request.Cancellation).ConfigureAwait(False)
                    outgoing.StatusCode = CInt(response.StatusCode)
                    For Each header In response.Headers.Concat(response.Content.Headers)
                        If SkipResponseHeaders.Contains(header.Key) Then Continue For
                        Try
                            If header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) Then
                                outgoing.ContentType = String.Join(", ", header.Value)
                            ElseIf header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) Then
                                outgoing.RedirectLocation = LocalLocation(String.Join(", ", header.Value))
                            Else
                                outgoing.Headers(header.Key) = String.Join(", ", header.Value)
                            End If
                        Catch ex As ArgumentException
                        End Try
                    Next
                    Dim media = If(response.Content.Headers.ContentType?.MediaType, "")
                    If media.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) Then
                        outgoing.SendChunked = True
                        outgoing.Headers("Cache-Control") = "no-cache"
                        Using stream = Await response.Content.ReadAsStreamAsync(request.Cancellation).ConfigureAwait(False)
                            Dim chunk(8191) As Byte
                            While True
                                Dim read = Await stream.ReadAsync(chunk, 0, chunk.Length, request.Cancellation).ConfigureAwait(False)
                                If read <= 0 Then Exit While
                                Await outgoing.OutputStream.WriteAsync(chunk, 0, read, request.Cancellation).ConfigureAwait(False)
                                Await outgoing.OutputStream.FlushAsync(request.Cancellation).ConfigureAwait(False)
                            End While
                        End Using
                        Return
                    End If
                    ' Everything but pages (the desktop layer goes in) and the API reference (its server address changes) is
                    ' streamed, not held whole in memory first: videos and songs play while they arrive and seek with byte
                    ' ranges (Range and 206 pass through), and Read aloud's speech (raw PCM) plays as the server makes it.
                    Dim isPage = media.Equals("text/html", StringComparison.OrdinalIgnoreCase) AndAlso Not request.RelativePath.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase)
                    If Not isPage AndAlso Not (isSpec AndAlso response.IsSuccessStatusCode) Then
                        Dim length = response.Content.Headers.ContentLength
                        If length.HasValue AndAlso response.Content.Headers.ContentEncoding.Count = 0 Then
                            outgoing.ContentLength64 = length.Value
                        Else
                            outgoing.SendChunked = True
                        End If
                        Using stream = Await response.Content.ReadAsStreamAsync(request.Cancellation).ConfigureAwait(False)
                            Await stream.CopyToAsync(outgoing.OutputStream, 81920, request.Cancellation).ConfigureAwait(False)
                        End Using
                        Return
                    End If
                    Dim bytes = Await response.Content.ReadAsByteArrayAsync(request.Cancellation).ConfigureAwait(False)
                    ' Still encoded (an encoding .NET did not decompress): pass it on untouched for the browser to decode.
                    Dim encoded = response.Content.Headers.ContentEncoding.Count > 0
                    If encoded Then
                    ElseIf isPage Then
                        bytes = Encoding.UTF8.GetBytes(InjectDesktopLayer(Encoding.UTF8.GetString(bytes)))
                        outgoing.Headers("Cache-Control") = "no-store"
                        outgoing.Headers.Remove("ETag")
                    ElseIf isSpec AndAlso response.IsSuccessStatusCode Then
                        bytes = Encoding.UTF8.GetBytes(PointSpecHere(Encoding.UTF8.GetString(bytes)))
                    End If
                    outgoing.ContentLength64 = bytes.Length
                    If bytes.Length > 0 Then Await outgoing.OutputStream.WriteAsync(bytes, 0, bytes.Length, request.Cancellation).ConfigureAwait(False)
                End Using
            End Using
        End Function

        ''' <summary>Adds the desktop layer after the web app's own scripts (deferred scripts run in order).</summary>
        Private Function InjectDesktopLayer(html As String) As String
            Dim at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase)
            If at < 0 Then Return html
            Dim version = GetType(ApiProxy).Assembly.GetName().Version
            Dim tags = $"  <meta name=""desktop-token"" content=""{DesktopToken}"">" & vbLf &
                       $"  <meta name=""desktop-version"" content=""{version}"">" & vbLf &
                       $"  <meta name=""desktop-device"" content=""{Net.WebUtility.HtmlEncode(_settings.DeviceId)}"">" & vbLf &
                       $"  <meta name=""desktop-machine"" content=""{Net.WebUtility.HtmlEncode(Environment.MachineName)}"">" & vbLf &
                       "  <link rel=""stylesheet"" href=""/desktop/desktop.css"">" & vbLf &
                       "  <script src=""/desktop/desktop.js"" defer></script>" & vbLf
            Return html.Insert(at, tags)
        End Function

        ''' <summary>A redirect to the server's own address stays inside the app.</summary>
        Private Function LocalLocation(location As String) As String
            Dim base = _settings.NormalizedBaseUrl()
            Return If(location.StartsWith(base, StringComparison.OrdinalIgnoreCase), location.Substring(base.Length), location)
        End Function

        ''' <summary>The API reference's "Try it out" targets this app (same origin, key added here).</summary>
        Private Shared Function PointSpecHere(specJson As String) As String
            Try
                Dim node = System.Text.Json.Nodes.JsonNode.Parse(specJson)
                If TypeOf node Is System.Text.Json.Nodes.JsonObject Then
                    node("servers") = New System.Text.Json.Nodes.JsonArray(New System.Text.Json.Nodes.JsonObject From {{"url", "/"}})
                    Return node.ToJsonString()
                End If
            Catch ex As System.Text.Json.JsonException
            End Try
            Return specJson
        End Function

    End Class

End Namespace
