' The desktop app's local web server (127.0.0.1 only):
'   /desktop/*    the desktop layer (desktop.js, desktop.css, the connect page), served from www\desktop
'   /api/local/*  desktop endpoints (settings, skills, local MCP, tools on this computer) - LocalApi
'   everything    else goes to the Agentic server through ApiProxy, which adds the API key, so the window shows the
'                 real web app (identical to the browser) plus the desktop layer.

Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Threading

Namespace Http

    ''' <summary>Per-request state passed to local handlers and the proxy.</summary>
    Public Class RequestContext

        Public Property ListenerContext As HttpListenerContext
        Public Property RelativePath As String
        Public Property Query As Dictionary(Of String, String)
        Public Property RouteSegments As New List(Of String)
        Public Property Cancellation As CancellationToken

        Public ReadOnly Property Method As String
            Get
                Return ListenerContext.Request.HttpMethod.ToUpperInvariant()
            End Get
        End Property

        Public Function Segment(index As Integer) As String
            Return If(index >= 0 AndAlso index < RouteSegments.Count, RouteSegments(index), Nothing)
        End Function

        Public Function QueryValue(name As String) As String
            Dim v As String = Nothing
            If Query.TryGetValue(name, v) Then Return v
            Return Nothing
        End Function

        Public Function BodyText() As String
            Using reader = New StreamReader(ListenerContext.Request.InputStream, Encoding.UTF8)
                Return reader.ReadToEnd()
            End Using
        End Function

        Public Function BodyJson() As System.Text.Json.JsonElement
            Dim text = BodyText()
            If String.IsNullOrWhiteSpace(text) Then text = "{}"
            Try
                Using doc = System.Text.Json.JsonDocument.Parse(text)
                    Return doc.RootElement.Clone()
                End Using
            Catch
                Using doc = System.Text.Json.JsonDocument.Parse("{}")
                    Return doc.RootElement.Clone()
                End Using
            End Try
        End Function

        ''' <summary>True for a page load (not a script's request).</summary>
        Public Function IsDocument() As Boolean
            Dim dest = ListenerContext.Request.Headers("Sec-Fetch-Dest")
            If dest IsNot Nothing Then Return dest = "document" OrElse dest = "iframe"
            Return Method = "GET" AndAlso If(ListenerContext.Request.Headers("Accept"), "").Contains("text/html")
        End Function

    End Class

    Public Class EmbeddedServer

        Private ReadOnly _proxy As ApiProxy
        Private ReadOnly _local As LocalApi
        Private ReadOnly _settings As AppSettings
        Private ReadOnly _token As String
        Private ReadOnly _wwwRoot As String
        Private _listener As HttpListener
        Private _cts As CancellationTokenSource
        Private _port As Integer

        Public Sub New(proxy As ApiProxy, local As LocalApi, settings As AppSettings, token As String)
            _proxy = proxy
            _local = local
            _settings = settings
            _token = token
            _wwwRoot = DesktopFiles.WwwRoot()
            _proxy.DesktopToken = token
        End Sub

        Public ReadOnly Property Origin As String
            Get
                Return $"http://127.0.0.1:{_port}"
            End Get
        End Property

        ''' <summary>Listens on the preferred port (stable, so the web app's saved preferences survive restarts) or any free one.</summary>
        Public Function Start(preferredPort As Integer) As String
            _cts = New CancellationTokenSource()
            For Each candidate In {preferredPort, 0}
                _port = If(candidate > 0, candidate, FreePort())
                _listener = New HttpListener()
                _listener.Prefixes.Add(Origin & "/")
                Try
                    _listener.Start()
                    Exit For
                Catch ex As HttpListenerException When candidate > 0
                    _listener.Close()
                End Try
            Next
            Dim unused = Task.Run(AddressOf AcceptLoopAsync)
            Return Origin & "/"
        End Function

        Private Shared Function FreePort() As Integer
            Using socket = New Net.Sockets.Socket(Net.Sockets.AddressFamily.InterNetwork, Net.Sockets.SocketType.Stream, Net.Sockets.ProtocolType.Tcp)
                socket.Bind(New IPEndPoint(IPAddress.Loopback, 0))
                Return DirectCast(socket.LocalEndPoint, IPEndPoint).Port
            End Using
        End Function

        Public Sub [Stop]()
            Try
                _cts?.Cancel()
                _listener?.Stop()
                _listener?.Close()
            Catch
            End Try
        End Sub

        Private Async Function AcceptLoopAsync() As Task
            While Not _cts.IsCancellationRequested
                Dim context As HttpListenerContext
                Try
                    context = Await _listener.GetContextAsync().ConfigureAwait(False)
                Catch ex As Exception When _cts.IsCancellationRequested OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is HttpListenerException
                    Exit While
                End Try
                Dim unused = Task.Run(Function() HandleSafeAsync(context))
            End While
        End Function

        Private Async Function HandleSafeAsync(context As HttpListenerContext) As Task
            Dim response = context.Response
            Try
                Dim request = ToRequest(context)
                ' Other web pages in a browser on this computer must not be able to use the app (it adds the API key and
                ' can run local tools): requests from another origin are refused.
                If Not FromThisApp(context.Request) Then
                    Await WriteErrorAsync(response, 403, "Requests from other web pages are not accepted.").ConfigureAwait(False)
                    Return
                End If
                Dim path = request.RelativePath
                If path.StartsWith("desktop/", StringComparison.OrdinalIgnoreCase) Then
                    Await ServeStaticAsync(request, path.Substring("desktop/".Length)).ConfigureAwait(False)
                ElseIf path.StartsWith("api/local/", StringComparison.OrdinalIgnoreCase) Then
                    If context.Request.Headers("X-Desktop-Token") <> _token Then
                        Await WriteErrorAsync(response, 403, "Missing desktop token.").ConfigureAwait(False)
                    Else
                        Await _local.HandleAsync(request).ConfigureAwait(False)
                    End If
                ElseIf Not _settings.HasKey() AndAlso request.IsDocument() Then
                    Redirect(response, "/desktop/connect.html?reason=nokey")
                Else
                    Dim unreachable As String = Nothing
                    Try
                        Await _proxy.ForwardAsync(request).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse (TypeOf ex Is TaskCanceledException AndAlso Not request.Cancellation.IsCancellationRequested)
                        unreachable = ex.Message
                    End Try
                    ' The Agentic server cannot be reached: pages go to the connect screen, scripts get a clear error.
                    If unreachable IsNot Nothing Then
                        If request.IsDocument() AndAlso Not path.StartsWith("sandbox", StringComparison.OrdinalIgnoreCase) Then
                            Redirect(response, "/desktop/connect.html?reason=offline&next=" & Uri.EscapeDataString("/" & path))
                        Else
                            Await WriteJsonAsync(response, 502, New With {.error = New With {.code = "SERVER_UNREACHABLE", .message = "The Agentic server cannot be reached: " & unreachable}}).ConfigureAwait(False)
                        End If
                    End If
                End If
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
                ' the page went away mid-response
            Catch ex As Exception
                Try
                    Dim unused = WriteErrorAsync(response, 500, ex.Message)
                Catch
                End Try
            Finally
                Try
                    response.Close()
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Same-origin requests and the window's own navigations only (Origin / Sec-Fetch-Site, sent by every modern browser).</summary>
        Private Function FromThisApp(request As HttpListenerRequest) As Boolean
            Dim origin = request.Headers("Origin")
            If Not String.IsNullOrEmpty(origin) AndAlso origin <> "null" AndAlso Not String.Equals(origin, Me.Origin, StringComparison.OrdinalIgnoreCase) Then Return False
            Dim site = request.Headers("Sec-Fetch-Site")
            If Not String.IsNullOrEmpty(site) AndAlso site <> "same-origin" AndAlso site <> "none" Then
                ' The sandboxed code-preview frame has an opaque origin; it only loads its own page.
                Return request.Url.AbsolutePath.StartsWith("/sandbox/", StringComparison.OrdinalIgnoreCase)
            End If
            Return True
        End Function

        Private Shared Sub Redirect(response As HttpListenerResponse, location As String)
            response.StatusCode = 302
            response.RedirectLocation = location
            response.AddHeader("Cache-Control", "no-store")
        End Sub

        Private Function ToRequest(context As HttpListenerContext) As RequestContext
            Dim path = context.Request.Url.AbsolutePath.TrimStart("/"c)
            Dim query As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim parsed = System.Web.HttpUtility.ParseQueryString(context.Request.Url.Query)
            For Each key In parsed.AllKeys
                If key IsNot Nothing Then query(key) = parsed(key)
            Next
            Return New RequestContext With {
                .ListenerContext = context, .RelativePath = path, .Query = query,
                .RouteSegments = path.Split("/"c, StringSplitOptions.RemoveEmptyEntries).Select(Function(s) Uri.UnescapeDataString(s)).ToList(),
                .Cancellation = _cts.Token}
        End Function

        ' ------------------------------------------------------------------ the desktop layer's static files

        Private Async Function ServeStaticAsync(request As RequestContext, relative As String) As Task
            Dim response = request.ListenerContext.Response
            Dim root = IO.Path.Combine(_wwwRoot, "desktop")
            Dim full = IO.Path.GetFullPath(IO.Path.Combine(root, Uri.UnescapeDataString(relative).Replace("/"c, IO.Path.DirectorySeparatorChar)))
            If Not PathsUtil.IsInside(full, root) OrElse Not File.Exists(full) Then
                Await WriteErrorAsync(response, 404, "Not found.").ConfigureAwait(False)
                Return
            End If
            Dim bytes = File.ReadAllBytes(full)
            If full.EndsWith(".html", StringComparison.OrdinalIgnoreCase) Then
                bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("{{DESKTOP_TOKEN}}", _token))
                response.AddHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'")
            End If
            Await WriteBytesAsync(response, 200, ContentType(IO.Path.GetExtension(full)), bytes, "no-cache").ConfigureAwait(False)
        End Function

        Friend Shared Function ContentType(extension As String) As String
            Select Case extension.ToLowerInvariant()
                Case ".html" : Return "text/html; charset=utf-8"
                Case ".css" : Return "text/css; charset=utf-8"
                Case ".js", ".mjs" : Return "text/javascript; charset=utf-8"
                Case ".json", ".map" : Return "application/json"
                Case ".svg" : Return "image/svg+xml"
                Case ".png" : Return "image/png"
                Case ".jpg", ".jpeg" : Return "image/jpeg"
                Case ".gif" : Return "image/gif"
                Case ".webp" : Return "image/webp"
                Case ".ico" : Return "image/x-icon"
                Case ".pdf" : Return "application/pdf"
                Case ".txt", ".log" : Return "text/plain; charset=utf-8"
                Case ".md" : Return "text/markdown; charset=utf-8"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

        ' ------------------------------------------------------------------ shared writers

        Public Shared Function WriteErrorAsync(response As HttpListenerResponse, status As Integer, message As String) As Task
            Return WriteJsonAsync(response, status, New With {.error = New With {.code = "HTTP_" & status, .message = message}})
        End Function

        Public Shared Async Function WriteJsonAsync(response As HttpListenerResponse, status As Integer, payload As Object) As Task
            Dim bytes = Encoding.UTF8.GetBytes(SimpleJson.Stringify(payload))
            response.StatusCode = status
            response.ContentType = "application/json; charset=utf-8"
            response.ContentLength64 = bytes.Length
            response.AddHeader("Cache-Control", "no-store")
            Await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(False)
        End Function

        Public Shared Async Function WriteBytesAsync(response As HttpListenerResponse, status As Integer, contentType As String, bytes As Byte(), cacheControl As String) As Task
            response.StatusCode = status
            response.ContentType = contentType
            response.ContentLength64 = bytes.Length
            response.AddHeader("Cache-Control", cacheControl)
            Await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(False)
        End Function

    End Class

End Namespace
