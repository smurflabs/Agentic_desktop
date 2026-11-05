Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Windows.Forms

''' <summary>The mouse, keyboard, windows and clipboard of this Windows computer (user32 SendInput and friends), for computer use.</summary>
Friend NotInheritable Class WinComputer
    Implements IComputerControl

    <StructLayout(LayoutKind.Sequential)>
    Private Structure MOUSEINPUT
        Public dx As Integer
        Public dy As Integer
        Public mouseData As Integer
        Public dwFlags As UInteger
        Public time As UInteger
        Public dwExtraInfo As IntPtr
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure KEYBDINPUT
        Public wVk As UShort
        Public wScan As UShort
        Public dwFlags As UInteger
        Public time As UInteger
        Public dwExtraInfo As IntPtr
    End Structure

    <StructLayout(LayoutKind.Explicit)>
    Private Structure InputUnion
        <FieldOffset(0)> Public mi As MOUSEINPUT
        <FieldOffset(0)> Public ki As KEYBDINPUT
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure INPUT
        Public type As UInteger
        Public u As InputUnion
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure POINT
        Public X As Integer
        Public Y As Integer
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure RECT
        Public Left As Integer
        Public Top As Integer
        Public Right As Integer
        Public Bottom As Integer
    End Structure

    Private Delegate Function EnumWindowsProc(hwnd As IntPtr, lParam As IntPtr) As Boolean

    <DllImport("user32.dll", SetLastError:=True)> Private Shared Function SendInput(n As UInteger, inputs As INPUT(), size As Integer) As UInteger
    End Function
    <DllImport("user32.dll")> Private Shared Function SetCursorPos(x As Integer, y As Integer) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function GetCursorPos(ByRef p As POINT) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function EnumWindows(cb As EnumWindowsProc, lParam As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll", CharSet:=CharSet.Unicode)> Private Shared Function GetWindowTextW(hwnd As IntPtr, sb As StringBuilder, max As Integer) As Integer
    End Function
    <DllImport("user32.dll")> Private Shared Function IsWindowVisible(hwnd As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function IsIconic(hwnd As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function GetWindowThreadProcessId(hwnd As IntPtr, ByRef pid As UInteger) As UInteger
    End Function
    <DllImport("user32.dll")> Private Shared Function GetWindowRect(hwnd As IntPtr, ByRef r As RECT) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function ShowWindow(hwnd As IntPtr, cmd As Integer) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function SetForegroundWindow(hwnd As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function PostMessage(hwnd As IntPtr, msg As UInteger, w As IntPtr, l As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Shared Function GetWindow(hwnd As IntPtr, cmd As UInteger) As IntPtr
    End Function

    Private Const INPUT_MOUSE As UInteger = 0, INPUT_KEYBOARD As UInteger = 1
    Private Const KEYEVENTF_KEYUP As UInteger = &H2, KEYEVENTF_UNICODE As UInteger = &H4
    Private Const MOUSEEVENTF_LEFTDOWN As UInteger = &H2, MOUSEEVENTF_LEFTUP As UInteger = &H4, MOUSEEVENTF_RIGHTDOWN As UInteger = &H8,
                  MOUSEEVENTF_RIGHTUP As UInteger = &H10, MOUSEEVENTF_MIDDLEDOWN As UInteger = &H20, MOUSEEVENTF_MIDDLEUP As UInteger = &H40, MOUSEEVENTF_WHEEL As UInteger = &H800

    Private Shared Sub Send(ParamArray inputs As INPUT())
        SendInput(CUInt(inputs.Length), inputs, Marshal.SizeOf(GetType(INPUT)))
    End Sub

    Private Shared Function Mouse(flags As UInteger, Optional data As Integer = 0) As INPUT
        Return New INPUT With {.type = INPUT_MOUSE, .u = New InputUnion With {.mi = New MOUSEINPUT With {.dwFlags = flags, .mouseData = data}}}
    End Function

    Private Shared Function Key(vk As UShort, up As Boolean) As INPUT
        Return New INPUT With {.type = INPUT_KEYBOARD, .u = New InputUnion With {.ki = New KEYBDINPUT With {.wVk = vk, .dwFlags = If(up, KEYEVENTF_KEYUP, 0UI)}}}
    End Function

    Public Function Problem() As String Implements IComputerControl.Problem
        Return Nothing
    End Function

    Public Sub MoveTo(x As Integer, y As Integer) Implements IComputerControl.MoveTo
        SetCursorPos(x, y)
    End Sub

    Public Sub Click(x As Integer, y As Integer, button As String, count As Integer) Implements IComputerControl.Click
        SetCursorPos(x, y)
        Thread.Sleep(40)
        Dim down = If(button = "right", MOUSEEVENTF_RIGHTDOWN, If(button = "middle", MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_LEFTDOWN))
        Dim up = If(button = "right", MOUSEEVENTF_RIGHTUP, If(button = "middle", MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_LEFTUP))
        For i = 1 To Math.Max(1, count)
            Send(Mouse(down), Mouse(up))
            If i < count Then Thread.Sleep(60)
        Next
    End Sub

    Public Sub Drag(x1 As Integer, y1 As Integer, x2 As Integer, y2 As Integer) Implements IComputerControl.Drag
        SetCursorPos(x1, y1)
        Thread.Sleep(50)
        Send(Mouse(MOUSEEVENTF_LEFTDOWN))
        For s = 1 To 12
            SetCursorPos(x1 + (x2 - x1) * s \ 12, y1 + (y2 - y1) * s \ 12)
            Thread.Sleep(15)
        Next
        Send(Mouse(MOUSEEVENTF_LEFTUP))
    End Sub

    Public Sub Scroll(x As Integer, y As Integer, amount As Integer) Implements IComputerControl.Scroll
        SetCursorPos(x, y)
        Thread.Sleep(30)
        Send(Mouse(MOUSEEVENTF_WHEEL, -120 * amount))
    End Sub

    Public Sub TypeText(text As String) Implements IComputerControl.TypeText
        For Each c In text.Replace(vbCrLf, vbLf)
            If c = ChrW(10) Then
                Send(Key(&HD, False), Key(&HD, True))
            Else
                Dim scan = CUShort(AscW(c) And &HFFFF)
                Send(New INPUT With {.type = INPUT_KEYBOARD, .u = New InputUnion With {.ki = New KEYBDINPUT With {.wScan = scan, .dwFlags = KEYEVENTF_UNICODE}}},
                     New INPUT With {.type = INPUT_KEYBOARD, .u = New InputUnion With {.ki = New KEYBDINPUT With {.wScan = scan, .dwFlags = KEYEVENTF_UNICODE Or KEYEVENTF_KEYUP}}})
            End If
            Thread.Sleep(4)
        Next
    End Sub

    ''' <summary>The virtual-key code of a key name (enter, ctrl, f5, a, 1, pagedown …), or 0.</summary>
    Friend Shared Function VirtualKey(name As String) As UShort
        Dim n = name.Trim().ToLowerInvariant()
        Select Case n
            Case "ctrl", "control" : Return &H11
            Case "shift" : Return &H10
            Case "alt" : Return &H12
            Case "win", "windows", "meta", "cmd", "super" : Return &H5B
            Case "enter", "return" : Return &HD
            Case "esc", "escape" : Return &H1B
            Case "tab" : Return &H9
            Case "space" : Return &H20
            Case "backspace" : Return &H8
            Case "delete", "del" : Return &H2E
            Case "insert", "ins" : Return &H2D
            Case "home" : Return &H24
            Case "end" : Return &H23
            Case "pageup", "pgup", "page_up" : Return &H21
            Case "pagedown", "pgdn", "page_down" : Return &H22
            Case "up", "arrowup" : Return &H26
            Case "down", "arrowdown" : Return &H28
            Case "left", "arrowleft" : Return &H25
            Case "right", "arrowright" : Return &H27
            Case "printscreen" : Return &H2C
        End Select
        If n.Length = 1 AndAlso Char.IsLetterOrDigit(n(0)) Then Return CUShort(AscW(Char.ToUpperInvariant(n(0))))
        If n.Length >= 2 AndAlso n(0) = "f"c AndAlso Integer.TryParse(n.Substring(1), Nothing) Then
            Dim f = Integer.Parse(n.Substring(1))
            If f >= 1 AndAlso f <= 24 Then Return CUShort(&H70 + f - 1)
        End If
        Dim k As Keys
        If [Enum].TryParse(name.Trim(), True, k) Then Return CUShort(CInt(k) And &HFF)
        Return 0
    End Function

    Public Sub PressKeys(keys As String) Implements IComputerControl.PressKeys
        Dim codes = keys.Split({"+"c}, StringSplitOptions.RemoveEmptyEntries).Select(Function(k) VirtualKey(k)).ToList()
        If codes.Count = 0 OrElse codes.Contains(0US) Then Throw New ArgumentException($"Unknown key in ""{keys}"". Use names like enter, tab, esc, ctrl+s, alt+tab, f5.")
        For Each c In codes
            Send(Key(c, False))
        Next
        For Each c In Enumerable.Reverse(codes)
            Send(Key(c, True))
        Next
    End Sub

    Public Function CursorPosition() As (X As Integer, Y As Integer) Implements IComputerControl.CursorPosition
        Dim p As POINT
        GetCursorPos(p)
        Return (p.X, p.Y)
    End Function

    Private Shared Function Title(h As IntPtr) As String
        Dim sb As New StringBuilder(512)
        GetWindowTextW(h, sb, sb.Capacity)
        Return sb.ToString()
    End Function

    Private Shared Function Windows() As List(Of IntPtr)
        Dim list As New List(Of IntPtr)
        EnumWindows(Function(h, l)
                        ' Visible top-level windows with a title (not tool windows owned by another).
                        If IsWindowVisible(h) AndAlso Title(h).Trim().Length > 0 AndAlso GetWindow(h, 4UI) = IntPtr.Zero Then list.Add(h)
                        Return True
                    End Function, IntPtr.Zero)
        Return list
    End Function

    Public Function ListWindows() As List(Of (Id As String, Title As String, App As String, Bounds As String)) Implements IComputerControl.ListWindows
        Dim out As New List(Of (String, String, String, String))
        For Each h In Windows().Take(80)
            Dim pid As UInteger
            GetWindowThreadProcessId(h, pid)
            Dim app = ""
            Try
                app = Process.GetProcessById(CInt(pid)).ProcessName
            Catch ex As ArgumentException
            End Try
            Dim r As RECT
            GetWindowRect(h, r)
            out.Add(("0x" & h.ToInt64().ToString("X"), Title(h), app, If(IsIconic(h), "minimized", $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}")))
        Next
        Return out
    End Function

    Public Function WindowAction(target As String, action As String) As String Implements IComputerControl.WindowAction
        Dim t = If(target, "").Trim()
        If t.Length = 0 Then Throw New ArgumentException("Give the window: its id or part of its title (list_windows).")
        Dim h = Windows().FirstOrDefault(Function(w) ("0x" & w.ToInt64().ToString("X")).Equals(t, StringComparison.OrdinalIgnoreCase))
        If h = IntPtr.Zero Then h = Windows().FirstOrDefault(Function(w) Title(w).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
        If h = IntPtr.Zero Then Throw New ArgumentException($"No window like ""{t}"". list_windows shows them.")
        Select Case action
            Case "minimize" : ShowWindow(h, 6)
            Case "maximize" : ShowWindow(h, 3)
            Case "restore" : ShowWindow(h, 9)
            Case "close" : PostMessage(h, &H10UI, IntPtr.Zero, IntPtr.Zero)
            Case Else
                If IsIconic(h) Then ShowWindow(h, 9)
                ' Windows lets a background app bring a window forward after a key press of its own.
                Send(Key(&H12, False), Key(&H12, True))
                SetForegroundWindow(h)
        End Select
        Return $"{If(action = "focus" OrElse String.IsNullOrEmpty(action), "Switched to", Char.ToUpperInvariant(action(0)) & action.Substring(1) & "d")} ""{Title(h)}""."
    End Function

    ''' <summary>The clipboard needs a single-threaded apartment thread.</summary>
    Private Shared Function OnSta(Of T)(work As Func(Of T)) As T
        Dim result As T = Nothing
        Dim failure As Exception = Nothing
        Dim th As New Thread(Sub()
                                 Try
                                     result = work()
                                 Catch ex As Exception
                                     failure = ex
                                 End Try
                             End Sub)
        th.SetApartmentState(ApartmentState.STA)
        th.Start()
        th.Join(5000)
        If failure IsNot Nothing Then Throw New InvalidOperationException("The clipboard is busy: " & failure.Message)
        Return result
    End Function

    Public Function GetClipboard() As String Implements IComputerControl.GetClipboard
        Return OnSta(Function() If(Clipboard.ContainsText(), Clipboard.GetText(), ""))
    End Function

    Public Sub SetClipboard(text As String) Implements IComputerControl.SetClipboard
        OnSta(Function()
                  If String.IsNullOrEmpty(text) Then Clipboard.Clear() Else Clipboard.SetText(text)
                  Return True
              End Function)
    End Sub

End Class
