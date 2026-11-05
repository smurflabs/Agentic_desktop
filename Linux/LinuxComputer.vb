Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics

''' <summary>
''' The mouse, keyboard, windows and clipboard of this Linux desktop (X11) for computer use, with xdotool, wmctrl and xclip
''' (or xsel / wl-clipboard). Nothing to install for the rest of the app: without xdotool computer use says what is missing.
''' </summary>
Friend NotInheritable Class LinuxComputer
    Implements IComputerControl

    Private Shared Function Has(tool As String) As Boolean
        Return PlatformUtil.Which(tool) IsNot Nothing
    End Function

    Private Shared Function Run(tool As String, ParamArray args As String()) As String
        Dim psi As New ProcessStartInfo(tool) With {.RedirectStandardOutput = True, .RedirectStandardError = True, .UseShellExecute = False}
        For Each a In args
            psi.ArgumentList.Add(a)
        Next
        Using p = Process.Start(psi)
            Dim output = p.StandardOutput.ReadToEnd()
            Dim errors = p.StandardError.ReadToEnd()
            If Not p.WaitForExit(15000) Then
                Try
                    p.Kill(True)
                Catch ex As InvalidOperationException
                End Try
                Throw New InvalidOperationException(tool & " did not finish.")
            End If
            If p.ExitCode <> 0 Then Throw New InvalidOperationException($"{tool} failed: {errors.Trim()}")
            Return output
        End Using
    End Function

    Public Function Problem() As String Implements IComputerControl.Problem
        If Not Has("xdotool") Then Return "Computer use needs xdotool (and wmctrl and xclip for windows and the clipboard): sudo apt install xdotool wmctrl xclip. It works on X11 desktops."
        Return Nothing
    End Function

    Private Shared Function Button(name As String) As String
        Return If(name = "right", "3", If(name = "middle", "2", "1"))
    End Function

    Public Sub MoveTo(x As Integer, y As Integer) Implements IComputerControl.MoveTo
        Run("xdotool", "mousemove", x.ToString(), y.ToString())
    End Sub

    Public Sub Click(x As Integer, y As Integer, buttonName As String, count As Integer) Implements IComputerControl.Click
        Run("xdotool", "mousemove", x.ToString(), y.ToString(), "click", "--repeat", Math.Max(1, count).ToString(), Button(buttonName))
    End Sub

    Public Sub Drag(x1 As Integer, y1 As Integer, x2 As Integer, y2 As Integer) Implements IComputerControl.Drag
        Run("xdotool", "mousemove", x1.ToString(), y1.ToString(), "mousedown", "1", "mousemove", x2.ToString(), y2.ToString(), "mouseup", "1")
    End Sub

    Public Sub Scroll(x As Integer, y As Integer, amount As Integer) Implements IComputerControl.Scroll
        Run("xdotool", "mousemove", x.ToString(), y.ToString(), "click", "--repeat", Math.Max(1, Math.Abs(amount)).ToString(), If(amount >= 0, "5", "4"))
    End Sub

    Public Sub TypeText(text As String) Implements IComputerControl.TypeText
        Run("xdotool", "type", "--delay", "8", "--", text)
    End Sub

    ''' <summary>Key names as X keysyms: enter → Return, esc → Escape, pagedown → Page_Down …</summary>
    Private Shared Function Keysym(name As String) As String
        Select Case name.Trim().ToLowerInvariant()
            Case "ctrl", "control" : Return "ctrl"
            Case "win", "windows", "meta", "cmd", "super" : Return "super"
            Case "enter", "return" : Return "Return"
            Case "esc", "escape" : Return "Escape"
            Case "backspace" : Return "BackSpace"
            Case "delete", "del" : Return "Delete"
            Case "pageup", "pgup", "page_up" : Return "Page_Up"
            Case "pagedown", "pgdn", "page_down" : Return "Page_Down"
            Case "up", "arrowup" : Return "Up"
            Case "down", "arrowdown" : Return "Down"
            Case "left", "arrowleft" : Return "Left"
            Case "right", "arrowright" : Return "Right"
            Case "space" : Return "space"
            Case "tab" : Return "Tab"
            Case "home" : Return "Home"
            Case "end" : Return "End"
            Case Else : Return name.Trim()
        End Select
    End Function

    Public Sub PressKeys(keys As String) Implements IComputerControl.PressKeys
        Run("xdotool", "key", String.Join("+", keys.Split({"+"c}, StringSplitOptions.RemoveEmptyEntries).Select(AddressOf Keysym)))
    End Sub

    Public Function CursorPosition() As (X As Integer, Y As Integer) Implements IComputerControl.CursorPosition
        Dim x = 0, y = 0
        For Each line In Run("xdotool", "getmouselocation", "--shell").Split(ChrW(10))
            If line.StartsWith("X=", StringComparison.Ordinal) Then Integer.TryParse(line.Substring(2), x)
            If line.StartsWith("Y=", StringComparison.Ordinal) Then Integer.TryParse(line.Substring(2), y)
        Next
        Return (x, y)
    End Function

    Public Function ListWindows() As List(Of (Id As String, Title As String, App As String, Bounds As String)) Implements IComputerControl.ListWindows
        Dim out As New List(Of (String, String, String, String))
        If Has("wmctrl") Then
            ' wmctrl -lpG: id desktop pid x y w h host title
            For Each line In Run("wmctrl", "-lpG").Split(ChrW(10)).Where(Function(l) l.Trim().Length > 0).Take(80)
                Dim parts = line.Split({" "c}, 9, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length < 9 Then Continue For
                Dim app = ""
                Try
                    app = Process.GetProcessById(Integer.Parse(parts(2))).ProcessName
                Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is FormatException
                End Try
                out.Add((parts(0), parts(8), app, $"{parts(3)},{parts(4)} {parts(5)}x{parts(6)}"))
            Next
        Else
            For Each id In Run("xdotool", "search", "--onlyvisible", "--name", ".").Split(ChrW(10)).Where(Function(l) l.Trim().Length > 0).Take(80)
                out.Add((id.Trim(), Run("xdotool", "getwindowname", id.Trim()).Trim(), "", ""))
            Next
        End If
        Return out
    End Function

    Public Function WindowAction(target As String, action As String) As String Implements IComputerControl.WindowAction
        Dim w = ListWindows().FirstOrDefault(Function(x) x.Id.Equals(target, StringComparison.OrdinalIgnoreCase) OrElse x.Title.IndexOf(If(target, ""), StringComparison.OrdinalIgnoreCase) >= 0)
        If w.Id Is Nothing Then Throw New ArgumentException($"No window like ""{target}"". list_windows shows them.")
        Select Case action
            Case "minimize" : Run("xdotool", "windowminimize", Convert.ToInt64(w.Id, 16).ToString())
            Case "maximize" : Run("wmctrl", "-i", "-r", w.Id, "-b", "add,maximized_vert,maximized_horz")
            Case "restore" : Run("wmctrl", "-i", "-r", w.Id, "-b", "remove,maximized_vert,maximized_horz")
            Case "close" : Run("wmctrl", "-i", "-c", w.Id)
            Case Else : If Has("wmctrl") Then Run("wmctrl", "-i", "-a", w.Id) Else Run("xdotool", "windowactivate", w.Id)
        End Select
        Return $"{If(action = "focus" OrElse String.IsNullOrEmpty(action), "Switched to", action)} ""{w.Title}""."
    End Function

    Public Function GetClipboard() As String Implements IComputerControl.GetClipboard
        If Has("xclip") Then Return Run("xclip", "-selection", "clipboard", "-o")
        If Has("xsel") Then Return Run("xsel", "--clipboard", "--output")
        If Has("wl-paste") Then Return Run("wl-paste", "--no-newline")
        Throw New InvalidOperationException("Reading the clipboard needs xclip, xsel or wl-clipboard.")
    End Function

    Public Sub SetClipboard(text As String) Implements IComputerControl.SetClipboard
        Dim tool = If(Has("xclip"), "xclip", If(Has("xsel"), "xsel", If(Has("wl-copy"), "wl-copy", Nothing)))
        If tool Is Nothing Then Throw New InvalidOperationException("Setting the clipboard needs xclip, xsel or wl-clipboard.")
        Dim psi As New ProcessStartInfo(tool) With {.RedirectStandardInput = True, .UseShellExecute = False}
        If tool = "xclip" Then psi.ArgumentList.Add("-selection") : psi.ArgumentList.Add("clipboard")
        If tool = "xsel" Then psi.ArgumentList.Add("--clipboard") : psi.ArgumentList.Add("--input")
        Using p = Process.Start(psi)
            p.StandardInput.Write(If(text, ""))
            p.StandardInput.Close()
            p.WaitForExit(5000)
        End Using
    End Sub

End Class
