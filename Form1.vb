Imports System.Drawing
Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms
Imports AgenticClient.Http
Imports AgenticClient.Win32
Imports Microsoft.Web.WebView2.Core

''' <summary>
''' The application window: a WebView2 that shows the Agentic web app through the local server (which adds the API key
''' and the desktop features). The title bar follows the web app's light or dark theme.
''' </summary>
Public Class Form1
    Inherits Form

    Private ReadOnly _settings As AppSettings
    Private ReadOnly _token As String = Guid.NewGuid().ToString("N")
    Private _server As EmbeddedServer
    Private _mcp As Mcp.McpHost
    Private _tools As LocalTools
    Private _updater As Updater
    Private ReadOnly _webView As Microsoft.Web.WebView2.WinForms.WebView2
    Private _theme As String
    Private _baseUrl As String
    Private _tray As NotifyIcon
    Private _quitting As Boolean
    Private _trayHintShown As Boolean
    ''' <summary>The chat the last notification was about: clicking it opens that chat.</summary>
    Private _notifiedChat As String
    Private ReadOnly _hotkeyProblems As New List(Of String)
    Private Const WM_HOTKEY As Integer = &H312
    Private Const HotkeyQuickEntry As Integer = 1
    Private Const HotkeyScreenshot As Integer = 2
    ''' <summary>Automated tests run the app minimized, without a tray icon or global shortcuts.</summary>
    Private ReadOnly _testMode As Boolean = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_MINIMIZED") = "1"
    ''' <summary>The browser environment of the window (the preview pane uses it too).</summary>
    Private _env As CoreWebView2Environment
    ''' <summary>The preview pane right of the chat (the project the user runs): a bar and the page.</summary>
    Private _previewPanel As Panel
    Private _previewBar As Panel
    Private _previewSplitter As Splitter
    Private _previewTitle As Label
    Private _preview As Microsoft.Web.WebView2.WinForms.WebView2
    Private _previewName As String
    Private ReadOnly _tips As New ToolTip()

    Public Sub New()
        _settings = AppSettingsStore.Load()
        _theme = ThemeUtil.Normalize(If(File.Exists(AppSettingsStore.SettingsFile()), _settings.LastTheme, If(ThemeUtil.SystemPrefersDark(), "dark", "light")))
        SuspendLayout()
        Text = "Agentic"
        StartPosition = FormStartPosition.CenterScreen
        ClientSize = New Size(1440, 900)
        MinimumSize = New Size(980, 620)
        ' Automated tests run the app without taking over the screen (AGENTIC_CLIENT_MINIMIZED=1).
        If Environment.GetEnvironmentVariable("AGENTIC_CLIENT_MINIMIZED") = "1" Then
            WindowState = FormWindowState.Minimized
            ShowInTaskbar = False
        End If
        BackColor = ThemeUtil.PageColor(_theme)
        Font = New Font("Segoe UI", 9.0F)
        _webView = New Microsoft.Web.WebView2.WinForms.WebView2 With {.Dock = DockStyle.Fill, .Name = "Web", .TabStop = True,
                                                                      .DefaultBackgroundColor = ThemeUtil.PageColor(_theme)}
        Controls.Add(_webView)
        CreatePreviewPane()
        Try
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        Catch
            Icon = SystemIcons.Application
        End Try
        ResumeLayout(False)
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        ApplyTitleBar()
        RegisterHotkeys()
    End Sub

    ' ------------------------------------------------------------------ global shortcuts, the notification-area icon, screenshots

    ''' <summary>(Re)registers the quick-entry and screenshot shortcuts; ones another app already uses are reported in Desktop settings.</summary>
    Private Sub RegisterHotkeys()
        If _testMode OrElse Not IsHandleCreated Then Return
        NativeMethods.UnregisterHotKey(Handle, HotkeyQuickEntry)
        NativeMethods.UnregisterHotKey(Handle, HotkeyScreenshot)
        _hotkeyProblems.Clear()
        For Each item In {(HotkeyQuickEntry, _settings.QuickEntryHotkey, "New chat"), (HotkeyScreenshot, _settings.ScreenshotHotkey, "Screenshot to chat")}
            Dim modifiers As UInteger, key As Keys
            If Not Hotkeys.TryParse(item.Item2, modifiers, key) Then Continue For
            If Not NativeMethods.RegisterHotKey(Handle, item.Item1, modifiers Or Hotkeys.ModNoRepeat, CUInt(key)) Then
                _hotkeyProblems.Add($"{item.Item3}: {item.Item2} is already used by another app. Choose another shortcut.")
            End If
        Next
    End Sub

    Protected Overrides Sub WndProc(ByRef m As Message)
        If m.Msg = WM_HOTKEY Then
            Select Case m.WParam.ToInt32()
                Case HotkeyQuickEntry : QuickEntry()
                Case HotkeyScreenshot : ScreenshotToChat()
            End Select
            Return
        End If
        MyBase.WndProc(m)
    End Sub

    Private Sub CreateTrayIcon()
        If _testMode Then Return
        Dim menu As New ContextMenuStrip()
        menu.Items.Add("Open Agentic", Nothing, Sub() ShowWindow())
        menu.Items.Add("New chat", Nothing, Sub() QuickEntry())
        menu.Items.Add("Screenshot to chat", Nothing, Sub() ScreenshotToChat())
        menu.Items.Add(New ToolStripSeparator())
        menu.Items.Add("Quit Agentic", Nothing, Sub() QuitApp())
        _tray = New NotifyIcon With {.Icon = Icon, .Text = "Agentic", .Visible = True, .ContextMenuStrip = menu}
        AddHandler _tray.MouseClick, Sub(s, e)
                                         If e.Button = MouseButtons.Left Then ShowWindow()
                                     End Sub
        AddHandler _tray.BalloonTipClicked, Sub(s, e) OpenNotifiedChat()
    End Sub

    ''' <summary>
    ''' A notification from the chat while the window is in the background (an answer finished, a chat needs you): Windows
    ''' shows it from the notification-area icon; clicking it brings the window back on that chat.
    ''' </summary>
    Private Sub ShowNotification(title As String, body As String, chatId As String)
        If _tray Is Nothing Then Return
        Dim id = If(chatId, "").Trim()
        _notifiedChat = If(id.Length > 0 AndAlso id.Length <= 64 AndAlso id.All(Function(ch) Char.IsLetterOrDigit(ch) OrElse ch = "-"c OrElse ch = "_"c), id, Nothing)
        Dim t = If(String.IsNullOrWhiteSpace(title), "Agentic", title.Trim())
        Dim b = If(String.IsNullOrWhiteSpace(body), "Agentic", body.Trim())
        _tray.ShowBalloonTip(6000, t.Substring(0, Math.Min(63, t.Length)), b.Substring(0, Math.Min(255, b.Length)), ToolTipIcon.None)
    End Sub

    Private Sub OpenNotifiedChat()
        ShowWindow()
        If _notifiedChat IsNot Nothing Then RunInPage($"window.dkOpenChat && window.dkOpenChat('{_notifiedChat}')")
    End Sub

    ''' <summary>Brings the window to the front (from the notification area, a shortcut or minimized).</summary>
    Private Sub ShowWindow()
        If Not Visible Then Show()
        If WindowState = FormWindowState.Minimized Then WindowState = FormWindowState.Normal
        Activate()
        NativeMethods.SetForegroundWindow(Handle)
        _webView.Focus()
    End Sub

    ''' <summary>The quick-entry shortcut: the window comes up on a new chat, ready to type.</summary>
    Private Sub QuickEntry()
        ShowWindow()
        RunInPage("window.dkQuickEntry && window.dkQuickEntry()")
    End Sub

    ''' <summary>The screenshot shortcut: the page asks for a screenshot and attaches it to the next message.</summary>
    Private Sub ScreenshotToChat()
        RunInPage("window.dkScreenshot && window.dkScreenshot()")
    End Sub

    Private Sub RunInPage(script As String)
        Try
            If _webView.CoreWebView2 IsNot Nothing Then
                Dim unused = _webView.CoreWebView2.ExecuteScriptAsync(script)
            End If
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Private Sub QuitApp()
        _quitting = True
        Close()
    End Sub

    ' ------------------------------------------------------------------ the preview pane (Run in Chat, the model's preview tool)

    ''' <summary>The (hidden) pane right of the chat: a bar with the page's title, Back, Reload, DevTools, Open in browser and Close; then the page.</summary>
    Private Sub CreatePreviewPane()
        ' Docking goes from the last control to the first: the pane takes the right edge, the splitter sits left of it, the chat fills the rest.
        _previewSplitter = New Splitter With {.Dock = DockStyle.Right, .Width = 5, .Visible = False, .MinSize = 320, .MinExtra = 480}
        _previewPanel = New Panel With {.Dock = DockStyle.Right, .Width = 640, .Visible = False}
        _previewBar = New Panel With {.Dock = DockStyle.Top, .Height = 36, .Padding = New Padding(10, 3, 4, 3)}
        _previewTitle = New Label With {.Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft, .AutoEllipsis = True, .Text = "Preview", .UseMnemonic = False}
        _previewBar.Controls.Add(_previewTitle)
        ' Docked right: the last one added sits furthest right.
        For Each item In {(&HE72B, "Back", CType(AddressOf PreviewBack, Action)), (&HE72C, "Reload", CType(AddressOf PreviewReload, Action)),
                          (&HEC7A, "Developer tools", CType(AddressOf PreviewDevTools, Action)), (&HE8A7, "Open in your browser", CType(AddressOf PreviewInBrowser, Action)),
                          (&HE711, "Close the preview (the project keeps running)", CType(AddressOf HidePreview, Action))}
            Dim act = item.Item3
            Dim b As New Button With {.Dock = DockStyle.Right, .Width = 34, .FlatStyle = FlatStyle.Flat, .Text = ChrW(item.Item1), .TabStop = False,
                                      .Font = New Font("Segoe MDL2 Assets", 10.0F), .AccessibleName = item.Item2, .Cursor = Cursors.Hand}
            b.FlatAppearance.BorderSize = 0
            AddHandler b.Click, Sub() act()
            _tips.SetToolTip(b, item.Item2)
            _previewBar.Controls.Add(b)
        Next
        _previewPanel.Controls.Add(_previewBar)
        Controls.Add(_previewSplitter)
        Controls.Add(_previewPanel)
        ApplyPreviewTheme()
    End Sub

    Private Sub ApplyPreviewTheme()
        If _previewBar Is Nothing Then Return
        _previewBar.BackColor = ThemeUtil.CaptionColor(_theme)
        _previewBar.ForeColor = ThemeUtil.CaptionTextColor(_theme)
        For Each b In _previewBar.Controls.OfType(Of Button)()
            b.FlatAppearance.MouseOverBackColor = ThemeUtil.BorderColor(_theme)
        Next
        _previewSplitter.BackColor = ThemeUtil.BorderColor(_theme)
        _previewPanel.BackColor = ThemeUtil.PageColor(_theme)
    End Sub

    ''' <summary>Shows a page in the preview pane next to the chat (any thread). The app's own pages are not shown there.</summary>
    Private Sub ShowPreview(url As String, title As String)
        If IsDisposed OrElse Not IsHandleCreated Then Return
        If InvokeRequired Then
            BeginInvoke(New Action(Sub() ShowPreview(url, title)))
            Return
        End If
        If Not IsWebAddress(url) OrElse (_baseUrl IsNot Nothing AndAlso url.StartsWith(_baseUrl & "/", StringComparison.OrdinalIgnoreCase)) Then Return
        ShowPreviewAsync(url, title)
    End Sub

    Private Shared Function IsWebAddress(url As String) As Boolean
        Dim u As Uri = Nothing
        Return Uri.TryCreate(url, UriKind.Absolute, u) AndAlso (u.Scheme = Uri.UriSchemeHttp OrElse u.Scheme = Uri.UriSchemeHttps)
    End Function

    Private Async Sub ShowPreviewAsync(url As String, title As String)
        Try
            If _env Is Nothing Then Return
            If _preview Is Nothing Then
                Dim view As New Microsoft.Web.WebView2.WinForms.WebView2 With {.Dock = DockStyle.Fill, .DefaultBackgroundColor = Color.White}
                _previewPanel.Controls.Add(view)
                view.BringToFront()
                Await view.EnsureCoreWebView2Async(_env)
                Dim core = view.CoreWebView2
                core.Settings.IsStatusBarEnabled = False
                ' The project's pages get no messages to the app (no WebMessageReceived) and stay on web addresses.
                AddHandler core.NewWindowRequested, AddressOf OnNewWindow
                AddHandler core.NavigationStarting, Sub(s, e)
                                                        If Not IsWebAddress(e.Uri) AndAlso Not e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) Then e.Cancel = True
                                                    End Sub
                AddHandler core.DocumentTitleChanged, Sub() UpdatePreviewTitle()
                AddHandler core.SourceChanged, Sub() UpdatePreviewTitle()
                _preview = view
            End If
            If Not _previewPanel.Visible Then
                _previewPanel.Width = Math.Max(360, CInt(ClientSize.Width * 0.45))
                _previewSplitter.Visible = True
                _previewPanel.Visible = True
            End If
            _previewName = If(String.IsNullOrWhiteSpace(title), Nothing, title.Trim())
            _preview.CoreWebView2.Navigate(url)
            UpdatePreviewTitle()
        Catch ex As Exception
            ' A page that cannot be shown leaves the chat as it was.
        End Try
    End Sub

    Private Sub UpdatePreviewTitle()
        Dim core = _preview?.CoreWebView2
        If core Is Nothing Then Return
        Dim u As Uri = Nothing
        Dim place = If(Uri.TryCreate(core.Source, UriKind.Absolute, u) AndAlso u.Scheme.StartsWith("http"), u.Authority & If(u.AbsolutePath = "/", "", u.AbsolutePath), "")
        Dim name = If(String.IsNullOrWhiteSpace(core.DocumentTitle) OrElse core.DocumentTitle = core.Source, _previewName, core.DocumentTitle)
        _previewTitle.Text = If(String.IsNullOrWhiteSpace(name), place, name & If(place.Length > 0, "   ·   " & place, ""))
        _tips.SetToolTip(_previewTitle, core.Source)
    End Sub

    Private Sub HidePreview()
        If _previewPanel Is Nothing OrElse Not _previewPanel.Visible Then Return
        _previewPanel.Visible = False
        _previewSplitter.Visible = False
        Try
            _preview?.CoreWebView2?.Navigate("about:blank")
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException
        End Try
        _webView.Focus()
    End Sub

    Private Sub PreviewBack()
        Dim core = _preview?.CoreWebView2
        If core IsNot Nothing AndAlso core.CanGoBack Then core.GoBack()
    End Sub

    Private Sub PreviewReload()
        _preview?.CoreWebView2?.Reload()
    End Sub

    Private Sub PreviewDevTools()
        _preview?.CoreWebView2?.OpenDevToolsWindow()
    End Sub

    Private Sub PreviewInBrowser()
        Dim url = _preview?.CoreWebView2?.Source
        If Not IsWebAddress(url) Then Return
        Try
            Process.Start(New ProcessStartInfo(url) With {.UseShellExecute = True})?.Dispose()
        Catch ex As System.ComponentModel.Win32Exception
        End Try
    End Sub

    ''' <summary>
    ''' Takes a screenshot (any thread): the window steps aside, then the user drags an area ("region"), or it takes the
    ''' monitor under the mouse ("screen"). For the model (take_screenshot): "monitor" or "all" monitors, without the user,
    ''' scaled down, and the window only comes back if it stepped aside for it.
    ''' </summary>
    Private Function CaptureAsync(area As String) As Task(Of Byte())
        Dim done As New TaskCompletionSource(Of Byte())(TaskCreationOptions.RunContinuationsAsynchronously)
        BeginInvoke(New Action(Sub() CaptureOnUiThread(area, done)))
        Return done.Task
    End Function

    ''' <summary>Where the model's last screenshot was on the screen and how much it was scaled down (computer use maps its pixels back).</summary>
    Private _lastShot As (X As Integer, Y As Integer, Scale As Double)?

    Private Async Sub CaptureOnUiThread(area As String, done As TaskCompletionSource(Of Byte()))
        Dim hidden = Visible AndAlso WindowState <> FormWindowState.Minimized
        Try
            If hidden Then
                Hide()
                Await Task.Delay(250) ' the window's close animation
            End If
            Dim shot = ScreenCapture.CaptureVirtualScreen()
            Dim result As Byte() = Nothing
            Using image = shot.Image
                Dim chosen As Rectangle? = Nothing
                If area = "screen" OrElse area = "monitor" Then
                    chosen = ScreenCapture.ScreenUnderCursor(shot.Bounds)
                ElseIf area = "all" Then
                    chosen = New Rectangle(Point.Empty, image.Size)
                Else
                    Dim wholeKey, cancelKey As Keys
                    If Not Hotkeys.TryParseKey(_settings.ScreenshotWholeScreenKey, wholeKey) Then wholeKey = Keys.Enter
                    If Not Hotkeys.TryParseKey(_settings.ScreenshotCancelKey, cancelKey) Then cancelKey = Keys.Escape
                    Using picker As New RegionPicker(image, shot.Bounds, wholeKey, cancelKey)
                        If picker.ShowDialog() = DialogResult.OK Then chosen = picker.Area
                    End Using
                End If
                If chosen.HasValue Then
                    Using part = ScreenCapture.Crop(image, chosen.Value)
                        If part IsNot Nothing Then
                            If area = "monitor" OrElse area = "all" Then
                                Dim small = ScreenCapture.ScaleDown(part, 1920)
                                result = ScreenCapture.ToPng(small)
                                _lastShot = (shot.Bounds.Left + chosen.Value.X, shot.Bounds.Top + chosen.Value.Y, small.Width / CDbl(Math.Max(1, part.Width)))
                                If small IsNot part Then small.Dispose()
                            Else
                                result = ScreenCapture.ToPng(part)
                            End If
                        End If
                    End Using
                End If
            End Using
            done.TrySetResult(result)
        Catch ex As Exception
            done.TrySetException(ex)
        Finally
            ' Back to the chat, where the screenshot now waits in the message box. A screenshot the model took brings the
            ' window back only if it stepped aside for it (it may be working in the background).
            If Not _testMode AndAlso (hidden OrElse (area <> "monitor" AndAlso area <> "all")) Then ShowWindow()
        End Try
    End Sub

    ''' <summary>
    ''' Dictation in Chat (and recording a voice on the Audio tab): the microphone, for the app's own pages (others get the
    ''' usual prompt). Dictation mode opens it again on its own after a pause, so the request need not follow a click.
    ''' </summary>
    Private Sub OnPermissionRequested(sender As Object, e As CoreWebView2PermissionRequestedEventArgs)
        If e.PermissionKind = CoreWebView2PermissionKind.Microphone AndAlso
           _baseUrl IsNot Nothing AndAlso e.Uri.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase) Then
            e.State = CoreWebView2PermissionState.Allow
        End If
    End Sub

    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e)
        StartAsync()
    End Sub

    ''' <summary>Title bar in the web app's colours (Windows 11 colours it; Windows 10 gets the dark or light bar).</summary>
    Private Sub ApplyTitleBar()
        Try
            NativeMethods.ApplyTitleBar(Handle, _theme = "dark", ThemeUtil.ToColorRef(ThemeUtil.CaptionColor(_theme)),
                                        ThemeUtil.ToColorRef(ThemeUtil.CaptionTextColor(_theme)), ThemeUtil.ToColorRef(ThemeUtil.BorderColor(_theme)))
        Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
            ' Very old Windows: the standard title bar stays.
        End Try
    End Sub

    Private Sub SetTheme(theme As String)
        theme = ThemeUtil.Normalize(theme)
        If theme = _theme Then Return
        _theme = theme
        BackColor = ThemeUtil.PageColor(theme)
        _webView.DefaultBackgroundColor = ThemeUtil.PageColor(theme)
        ApplyTitleBar()
        ApplyPreviewTheme()
        _settings.LastTheme = theme
        AppSettingsStore.Save(_settings)
    End Sub

    Private Async Sub StartAsync()
        Dim skills As New SkillStore(_settings)
        _mcp = New Mcp.McpHost()
        Dim tools As New LocalTools(_settings) With {.Checkpoints = New FileCheckpoints(IO.Path.Combine(PathsUtil.AppDataRoot(), "checkpoints")),
                                                     .ShowPreview = AddressOf ShowPreview}
        _tools = tools
        _updater = New Updater(_settings)
        Dim local As New LocalApi(skills, _mcp, tools, _settings) With {
            .Updates = _updater, .RestartRequested = Sub() BeginInvoke(New Action(AddressOf RestartForUpdate)),
            .CaptureScreen = AddressOf CaptureAsync,
            .Computer = New WinComputer(), .ScreenMap = Function() _lastShot,
            .SettingsChanged = Sub() BeginInvoke(New Action(AddressOf RegisterHotkeys)),
            .HotkeyProblems = Function() _hotkeyProblems.ToList(), .NormalizeHotkey = AddressOf Hotkeys.Normalize, .NormalizeKey = AddressOf Hotkeys.NormalizeKey}
        _server = New EmbeddedServer(New ApiProxy(_settings), local, _settings, _token)
        Dim baseUrl As String
        Try
            baseUrl = _server.Start(_settings.Port)
        Catch ex As Exception
            MessageBox.Show("The desktop app could not start its local server: " & ex.Message, "Agentic", MessageBoxButtons.OK, MessageBoxIcon.Error)
            QuitApp()
            Return
        End Try
        _baseUrl = baseUrl.TrimEnd("/"c)
        CreateTrayIcon()
        ' Local MCP servers connect in the background; the window does not wait for them.
        Dim unused = Task.Run(AddressOf _mcp.ConnectAllAsync)
        ' Brings www\desktop up to date before the page loads (a few seconds at most) and installs a newer exe in the background.
        Dim updating = _updater.StartAsync()

        Try
            ' Notification sounds (user menu → Sounds) play while the window is in the background, without a click first.
            ' AGENTIC_CLIENT_DEBUG_PORT opens the Chromium remote-debugging port (DevTools / automated tests);
            ' AGENTIC_CLIENT_BROWSER_ARGS adds Chromium switches (automated tests: a fake microphone).
            Dim browserArgs = "--autoplay-policy=no-user-gesture-required"
            Dim debugPort As Integer
            If Integer.TryParse(Environment.GetEnvironmentVariable("AGENTIC_CLIENT_DEBUG_PORT"), debugPort) AndAlso debugPort > 0 Then
                browserArgs &= $" --remote-debugging-port={debugPort}"
            End If
            Dim extraArgs = Environment.GetEnvironmentVariable("AGENTIC_CLIENT_BROWSER_ARGS")
            If Not String.IsNullOrWhiteSpace(extraArgs) Then browserArgs &= " " & extraArgs.Trim()
            Dim options As New CoreWebView2EnvironmentOptions(browserArgs)
            Dim env = Await CoreWebView2Environment.CreateAsync(Nothing, PathsUtil.EnsureDir(Path.Combine(PathsUtil.AppDataRoot(), "WebView2")), options)
            _env = env
            Await _webView.EnsureCoreWebView2Async(env)
            Dim core = _webView.CoreWebView2
            core.Settings.IsStatusBarEnabled = False
            core.Settings.AreDefaultContextMenusEnabled = True
            core.Settings.IsZoomControlEnabled = True
            AddHandler core.NewWindowRequested, AddressOf OnNewWindow
            AddHandler core.WebMessageReceived, AddressOf OnWebMessage
            AddHandler core.PermissionRequested, AddressOf OnPermissionRequested
            AddHandler core.DocumentTitleChanged, Sub() Text = If(String.IsNullOrWhiteSpace(core.DocumentTitle), "Agentic", core.DocumentTitle.Replace("Agentic Console", "Agentic"))
            Await updating
            Dim start = If(String.IsNullOrWhiteSpace(_settings.StartPage), "/chat", _settings.StartPage)
            _webView.Source = New Uri(baseUrl.TrimEnd("/"c) & If(start.StartsWith("/"), start, "/" & start))
        Catch ex As Exception
            MessageBox.Show("WebView2 failed to start: " & ex.Message & vbCrLf & vbCrLf &
                            "Install the WebView2 Runtime: https://developer.microsoft.com/microsoft-edge/webview2/",
                            "Agentic", MessageBoxButtons.OK, MessageBoxIcon.Error)
            QuitApp()
        End Try
    End Sub

    ''' <summary>"Restart to update": the new exe starts once this window has closed.</summary>
    Private Sub RestartForUpdate()
        Try
            _updater.StartNewInstance()
        Catch ex As System.ComponentModel.Win32Exception
            MessageBox.Show("The updated app could not be started: " & ex.Message, "Agentic", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End Try
        QuitApp()
    End Sub

    ''' <summary>Links that open a new window go to the default browser (web links only).</summary>
    Private Sub OnNewWindow(sender As Object, e As CoreWebView2NewWindowRequestedEventArgs)
        e.Handled = True
        Dim uri As Uri = Nothing
        If Uri.TryCreate(e.Uri, UriKind.Absolute, uri) AndAlso (uri.Scheme = Uri.UriSchemeHttp OrElse uri.Scheme = Uri.UriSchemeHttps) Then
            Try
                Process.Start(New ProcessStartInfo(uri.AbsoluteUri) With {.UseShellExecute = True})
            Catch
            End Try
        End If
    End Sub

    ''' <summary>Messages from desktop.js: {kind: "theme", theme: "dark"|"light"}, {kind: "attention"}, {kind: "notify", title, body, chatId}.</summary>
    Private Sub OnWebMessage(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
        Try
            Using doc = JsonDocument.Parse(e.WebMessageAsJson)
                Dim r = doc.RootElement
                If r.ValueKind <> JsonValueKind.Object Then Return
                Select Case AppSettingsStore.Str(r, "kind", "")
                    Case "theme" : SetTheme(AppSettingsStore.Str(r, "theme", "dark"))
                    Case "attention" : NativeMethods.FlashUntilFocused(Handle)
                    Case "notify" : ShowNotification(AppSettingsStore.Str(r, "title", "Agentic"), AppSettingsStore.Str(r, "body", ""), AppSettingsStore.Str(r, "chatId", ""))
                End Select
            End Using
        Catch ex As JsonException
        End Try
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' "Keep running in the notification area": closing the window only hides it.
        If _settings.CloseToTray AndAlso _tray IsNot Nothing AndAlso Not _quitting AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            Hide()
            If Not _trayHintShown Then
                _trayHintShown = True
                _tray.ShowBalloonTip(4000, "Agentic is still running", "Open it from here, or with " &
                                     If(String.IsNullOrEmpty(_settings.QuickEntryHotkey), "this icon", _settings.QuickEntryHotkey) & ". Quit from this icon's menu.", ToolTipIcon.Info)
            End If
            Return
        End If
        If IsHandleCreated Then
            NativeMethods.UnregisterHotKey(Handle, HotkeyQuickEntry)
            NativeMethods.UnregisterHotKey(Handle, HotkeyScreenshot)
        End If
        If _tray IsNot Nothing Then
            _tray.Visible = False
            _tray.Dispose()
        End If
        Try
            _preview?.Dispose()
            _webView?.Dispose()
        Catch
        End Try
        _server?.Stop()
        _mcp?.Dispose()
        _tools?.StopBackgroundCommands()
        _tools?.StopRuns()
        MyBase.OnFormClosing(e)
    End Sub

End Class
