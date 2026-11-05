Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.Text.Json
Imports System.Text.Json.Nodes

''' <summary>Mouse, keyboard, windows and the clipboard of this computer (Windows: user32; Linux: xdotool, wmctrl, xclip).</summary>
Public Interface IComputerControl
    ''' <summary>Why it cannot be used here (a missing tool), or Nothing when it can.</summary>
    Function Problem() As String
    Sub MoveTo(x As Integer, y As Integer)
    ''' <summary>button: left | right | middle; count 2 for a double click.</summary>
    Sub Click(x As Integer, y As Integer, button As String, count As Integer)
    Sub Drag(x1 As Integer, y1 As Integer, x2 As Integer, y2 As Integer)
    ''' <summary>Scrolls at a point: positive down, negative up (wheel clicks).</summary>
    Sub Scroll(x As Integer, y As Integer, amount As Integer)
    Sub TypeText(text As String)
    ''' <summary>A key or combination: "enter", "ctrl+s", "alt+tab", "ctrl+shift+t".</summary>
    Sub PressKeys(keys As String)
    Function CursorPosition() As (X As Integer, Y As Integer)
    Function ListWindows() As List(Of (Id As String, Title As String, App As String, Bounds As String))
    ''' <summary>focus | minimize | maximize | restore | close; the first window whose id is that, or whose title contains it.</summary>
    Function WindowAction(target As String, action As String) As String
    Function GetClipboard() As String
    Sub SetClipboard(text As String)
End Interface

''' <summary>
''' Computer use for the chat model (Desktop settings → "Let the model use the mouse and keyboard", off by default): look at
''' the screen (take_screenshot), click, type, press keys, scroll and drag on it, list and switch windows, read and set the
''' clipboard, and open files, apps and web pages. Every action asks first in the chat (in Auto mode too) unless the user
''' chose Always allow there. Coordinates are pixels of the last screenshot the model took; after each action it gets a new
''' screenshot to see what happened.
''' </summary>
Public NotInheritable Class ComputerTools

    Private Sub New()
    End Sub

    Public Shared ReadOnly Names As String() = {"computer", "list_windows", "window", "clipboard", "open_on_computer"}

    Private Shared Function Def(name As String, risk As String, description As String, schema As String) As JsonObject
        Return New JsonObject From {{"name", name}, {"x-risk", risk}, {"description", description}, {"parameters", JsonNode.Parse(schema)}}
    End Function

    Public Shared Function Definitions() As List(Of JsonObject)
        Return New List(Of JsonObject) From {
            Def("computer", "ask",
                "Use the mouse and keyboard of the user's computer. Take a screenshot first (take_screenshot): x and y are pixels of the latest screenshot. " &
                "action: click, double_click, right_click, middle_click, move, drag (x,y to to_x,to_y), scroll (amount: wheel clicks, positive down), type (text), " &
                "key (keys like enter, ctrl+s, alt+tab), cursor_position, wait (seconds). A new screenshot comes back after the action (screenshot false: none).",
                "{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""click"",""double_click"",""right_click"",""middle_click"",""move"",""drag"",""scroll"",""type"",""key"",""cursor_position"",""wait""]}," &
                """x"":{""type"":""integer""},""y"":{""type"":""integer""},""to_x"":{""type"":""integer""},""to_y"":{""type"":""integer""},""amount"":{""type"":""integer""}," &
                """text"":{""type"":""string""},""keys"":{""type"":""string""},""seconds"":{""type"":""number""},""screenshot"":{""type"":""boolean""}},""required"":[""action""]}"),
            Def("list_windows", "read", "The open windows on the user's computer: id, title, app and position.", "{""type"":""object"",""properties"":{}}"),
            Def("window", "ask", "Switch to (focus), minimize, maximize, restore or close a window on the user's computer, by its id or part of its title (list_windows).",
                "{""type"":""object"",""properties"":{""window"":{""type"":""string""},""action"":{""type"":""string"",""enum"":[""focus"",""minimize"",""maximize"",""restore"",""close""]}},""required"":[""window"",""action""]}"),
            Def("clipboard", "ask", "Read (get) or set the clipboard's text on the user's computer.",
                "{""type"":""object"",""properties"":{""action"":{""type"":""string"",""enum"":[""get"",""set""]},""text"":{""type"":""string""}},""required"":[""action""]}"),
            Def("open_on_computer", "ask", "Open a web page in the user's browser, a file with its app, or start an app on the user's computer.",
                "{""type"":""object"",""properties"":{""target"":{""type"":""string"",""description"":""A URL, a file path or an app (notepad, code, calc …)""},""args"":{""type"":""string""}},""required"":[""target""]}")}
    End Function

    Private Shared Function Str(a As JsonElement, n As String, Optional d As String = Nothing) As String
        Dim el As JsonElement
        Return If(a.ValueKind = JsonValueKind.Object AndAlso a.TryGetProperty(n, el) AndAlso el.ValueKind = JsonValueKind.String, el.GetString(), d)
    End Function

    Private Shared Function Num(a As JsonElement, n As String) As Double?
        Dim el As JsonElement
        If a.ValueKind = JsonValueKind.Object AndAlso a.TryGetProperty(n, el) AndAlso el.ValueKind = JsonValueKind.Number Then Return el.GetDouble()
        Return Nothing
    End Function

    ''' <summary>
    ''' Runs one of the tools. map: where the last screenshot was on the screen and how much it was scaled down (Nothing before
    ''' the first one). shoot: takes a new screenshot (PNG) of the screen the mouse is on.
    ''' </summary>
    Public Shared Async Function RunAsync(name As String, a As JsonElement, control As IComputerControl,
                                          map As Func(Of (X As Integer, Y As Integer, Scale As Double)?), shoot As Func(Of Task(Of Byte()))) As Task(Of (Ok As Boolean, Text As String, Png As Byte()))
        If control Is Nothing Then Return (False, "Computer use is not available in this app.", Nothing)
        Dim problem = control.Problem()
        If problem IsNot Nothing Then Return (False, problem, Nothing)
        Try
            Select Case name
                Case "list_windows"
                    Dim list = control.ListWindows()
                    Return (True, If(list.Count = 0, "No windows.", String.Join(vbLf, list.Select(Function(w) $"{w.Id}  {w.Title}  ({w.App}{If(String.IsNullOrEmpty(w.Bounds), "", ", " & w.Bounds)})"))), Nothing)
                Case "window"
                    Return (True, control.WindowAction(If(Str(a, "window"), ""), If(Str(a, "action"), "focus")), Nothing)
                Case "clipboard"
                    If Str(a, "action") = "set" Then
                        control.SetClipboard(If(Str(a, "text"), ""))
                        Return (True, "The clipboard now holds the text.", Nothing)
                    End If
                    Dim t = control.GetClipboard()
                    Return (True, If(String.IsNullOrEmpty(t), "(the clipboard has no text)", If(t.Length > 20000, t.Substring(0, 20000) & vbLf & "… (shortened)", t)), Nothing)
                Case "open_on_computer"
                    Dim target = If(Str(a, "target"), "").Trim()
                    If target.Length = 0 Then Return (False, "Give target: a URL, a file or an app.", Nothing)
                    If OperatingSystem.IsWindows() Then
                        Process.Start(New ProcessStartInfo(target, If(Str(a, "args"), "")) With {.UseShellExecute = True})
                    Else
                        Dim psi As New ProcessStartInfo("xdg-open")
                        psi.ArgumentList.Add(target)
                        Process.Start(psi)
                    End If
                    Return (True, "Opened " & target & ".", Nothing)
            End Select

            ' computer: coordinates of the last screenshot → the screen's.
            Dim action = If(Str(a, "action"), "")
            Dim m = map()
            Dim toScreen = Function(xn As String, yn As String) As (X As Integer, Y As Integer)?
                               Dim x = Num(a, xn), y = Num(a, yn)
                               If Not x.HasValue OrElse Not y.HasValue Then Return Nothing
                               If Not m.HasValue Then Return (CInt(x.Value), CInt(y.Value))
                               Return (m.Value.X + CInt(Math.Round(x.Value / m.Value.Scale)), m.Value.Y + CInt(Math.Round(y.Value / m.Value.Scale)))
                           End Function
            Dim p = toScreen("x", "y")
            Dim needPoint = {"click", "double_click", "right_click", "middle_click", "move", "drag"}
            If needPoint.Contains(action) AndAlso Not p.HasValue Then Return (False, "Give x and y (pixels of the latest screenshot; take_screenshot first).", Nothing)
            If needPoint.Contains(action) AndAlso Not m.HasValue Then Return (False, "Take a screenshot first (take_screenshot): x and y are pixels of it.", Nothing)
            Dim said As String
            Select Case action
                Case "click" : control.Click(p.Value.X, p.Value.Y, "left", 1) : said = $"Clicked at ({Num(a, "x")}, {Num(a, "y")})."
                Case "double_click" : control.Click(p.Value.X, p.Value.Y, "left", 2) : said = $"Double-clicked at ({Num(a, "x")}, {Num(a, "y")})."
                Case "right_click" : control.Click(p.Value.X, p.Value.Y, "right", 1) : said = $"Right-clicked at ({Num(a, "x")}, {Num(a, "y")})."
                Case "middle_click" : control.Click(p.Value.X, p.Value.Y, "middle", 1) : said = $"Middle-clicked at ({Num(a, "x")}, {Num(a, "y")})."
                Case "move" : control.MoveTo(p.Value.X, p.Value.Y) : said = $"Moved the mouse to ({Num(a, "x")}, {Num(a, "y")})."
                Case "drag"
                    Dim q = toScreen("to_x", "to_y")
                    If Not q.HasValue Then Return (False, "Give to_x and to_y.", Nothing)
                    control.Drag(p.Value.X, p.Value.Y, q.Value.X, q.Value.Y)
                    said = $"Dragged from ({Num(a, "x")}, {Num(a, "y")}) to ({Num(a, "to_x")}, {Num(a, "to_y")})."
                Case "scroll"
                    Dim at = If(p, control.CursorPosition())
                    Dim amount = CInt(If(Num(a, "amount"), 3))
                    control.Scroll(at.X, at.Y, amount)
                    said = $"Scrolled {If(amount >= 0, "down", "up")} {Math.Abs(amount)}."
                Case "type"
                    Dim text = If(Str(a, "text"), "")
                    If text.Length = 0 Then Return (False, "Give text to type.", Nothing)
                    control.TypeText(text)
                    said = $"Typed {text.Length} characters."
                Case "key"
                    Dim keys = If(Str(a, "keys"), Str(a, "text"))
                    If String.IsNullOrWhiteSpace(keys) Then Return (False, "Give keys, e.g. enter or ctrl+s.", Nothing)
                    control.PressKeys(keys)
                    said = "Pressed " & keys & "."
                Case "cursor_position"
                    Dim c = control.CursorPosition()
                    If m.HasValue Then
                        Return (True, $"The mouse is at ({CInt((c.X - m.Value.X) * m.Value.Scale)}, {CInt((c.Y - m.Value.Y) * m.Value.Scale)}) in the latest screenshot.", Nothing)
                    End If
                    Return (True, $"The mouse is at ({c.X}, {c.Y}) on the screen.", Nothing)
                Case "wait"
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(If(Num(a, "seconds"), 1), 0.1, 30))).ConfigureAwait(False)
                    said = "Waited."
                Case Else
                    Return (False, "Unknown action " & action & ".", Nothing)
            End Select
            ' A new screenshot shows what happened (unless the model said screenshot false).
            Dim el As JsonElement
            Dim wantShot = Not (a.TryGetProperty("screenshot", el) AndAlso el.ValueKind = JsonValueKind.False)
            If Not wantShot OrElse shoot Is Nothing Then Return (True, said, Nothing)
            Await Task.Delay(400).ConfigureAwait(False)
            Dim png = Await shoot().ConfigureAwait(False)
            Return (True, said & If(png Is Nothing, "", " The screen now follows as a picture (its pixels are the coordinates for the next action)."), png)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is IO.IOException
            Return (False, ex.Message, Nothing)
        End Try
    End Function

End Class
