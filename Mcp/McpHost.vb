Namespace Mcp

    ''' <summary>
    ''' Owns the local MCP servers: connects the enabled ones from mcp.json in parallel, reconnects one on request, and
    ''' answers status, tool and call queries from the desktop layer and the chat.
    ''' </summary>
    Public Class McpHost
        Implements IDisposable

        Private ReadOnly _lock As New Object()
        Private ReadOnly _connections As New Dictionary(Of String, McpConnection)(StringComparer.OrdinalIgnoreCase)

        Public Async Function ConnectAllAsync() As Task
            ' A project's servers start only once allowed (the MCP page asks).
            Dim tasks = McpConfig.Load().Where(AddressOf McpConfig.MayStart).Select(AddressOf ConnectOneAsync).ToArray()
            If tasks.Length > 0 Then Await Task.WhenAll(tasks).ConfigureAwait(False)
        End Function

        Private Async Function ConnectOneAsync(options As McpServerOptions) As Task
            Dim connection As New McpConnection(options) With {.Roots = If(options.Root IsNot Nothing, New List(Of String) From {options.Root}, McpConfig.Workspaces.Invoke())}
            Dim previous As McpConnection = Nothing
            SyncLock _lock
                _connections.TryGetValue(options.Id, previous)
                _connections(options.Id) = connection
            End SyncLock
            previous?.Dispose()
            Await connection.StartAsync().ConfigureAwait(False)
        End Function

        ''' <summary>Connects (again) with the current config; a disabled or removed server is disconnected.</summary>
        Public Async Function ReconnectAsync(serverId As String) As Task
            Dim options = McpConfig.Load().FirstOrDefault(Function(s) s.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase))
            If options Is Nothing OrElse Not McpConfig.MayStart(options) Then
                Disconnect(serverId)
                Return
            End If
            Await ConnectOneAsync(options).ConfigureAwait(False)
        End Function

        Public Sub Disconnect(serverId As String)
            Dim c As McpConnection = Nothing
            SyncLock _lock
                If _connections.TryGetValue(serverId, c) Then _connections.Remove(serverId)
            End SyncLock
            c?.Dispose()
        End Sub

        ''' <summary>Every configured server with its live status (servers that never connected show as disconnected).</summary>
        Public Function Snapshot() As List(Of Object)
            Dim configured = McpConfig.Load()
            Dim list As New List(Of Object)
            SyncLock _lock
                For Each o In configured
                    Dim c As McpConnection = Nothing
                    Dim status = If(_connections.TryGetValue(o.Id, c), c.Status, New McpServerStatus With {.Id = o.Id, .Name = o.Name, .Transport = o.TransportName()})
                    ' Where it comes from; only this app's own servers are edited here (the others: on/off, allow, sign in).
                    list.Add(New With {.status = status.ToJson(), .config = o.ToConfig(), .enabled = o.Enabled, .requireApproval = o.RequireApproval,
                                       .source = If(o.Source, "This app"), .scope = o.Scope, .editable = o.Scope = "user", .approval = McpConfig.Approval(o),
                                       .signedIn = o.Transport <> McpTransportKind.Stdio AndAlso McpOAuth.SignedIn(McpConfig.Expand(o.Endpoint, o.Root, o.PluginRoot))})
                Next
            End SyncLock
            Return list
        End Function

        Public Function ConnectedServers() As List(Of McpConnection)
            SyncLock _lock
                Return _connections.Values.Where(Function(c) c.Status.Connected).ToList()
            End SyncLock
        End Function

        Public Function [Get](serverId As String) As McpConnection
            SyncLock _lock
                Dim c As McpConnection = Nothing
                Return If(_connections.TryGetValue(serverId, c), c, Nothing)
            End SyncLock
        End Function

        Public Async Function CallToolAsync(serverId As String, toolName As String, argumentsJson As String) As Task(Of McpToolResult)
            Dim connection = [Get](serverId)
            If connection Is Nothing OrElse Not connection.Status.Connected Then
                Return New McpToolResult With {.Ok = False, .IsError = True, .Error = $"MCP server '{serverId}' is not connected."}
            End If
            Return Await connection.CallToolAsync(toolName, argumentsJson).ConfigureAwait(False)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim all As List(Of McpConnection)
            SyncLock _lock
                all = _connections.Values.ToList()
                _connections.Clear()
            End SyncLock
            For Each c In all
                c.Dispose()
            Next
        End Sub

    End Class

End Namespace
