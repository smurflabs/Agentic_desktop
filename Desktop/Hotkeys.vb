Imports System.Windows.Forms

''' <summary>Global keyboard shortcuts written like "Ctrl+Alt+Space" (modifiers + one key).</summary>
Friend Module Hotkeys

    Public Const ModAlt As UInteger = &H1UI
    Public Const ModControl As UInteger = &H2UI
    Public Const ModShift As UInteger = &H4UI
    Public Const ModWin As UInteger = &H8UI
    ''' <summary>Holding the keys down does not repeat the shortcut.</summary>
    Public Const ModNoRepeat As UInteger = &H4000UI

    ''' <summary>
    ''' Reads a shortcut: at least one of Ctrl, Alt, Shift, Win and one key (A-Z, 0-9, F1-F24, Space, or another key name).
    ''' False when it is empty or not a shortcut.
    ''' </summary>
    Public Function TryParse(text As String, ByRef modifiers As UInteger, ByRef key As Keys) As Boolean
        modifiers = 0
        key = Keys.None
        If String.IsNullOrWhiteSpace(text) Then Return False
        Dim parts = text.Split("+"c).Select(Function(p) p.Trim()).Where(Function(p) p.Length > 0).ToList()
        If parts.Count < 2 Then Return False
        For Each p In parts.Take(parts.Count - 1)
            Select Case p.ToLowerInvariant()
                Case "ctrl", "control" : modifiers = modifiers Or ModControl
                Case "alt" : modifiers = modifiers Or ModAlt
                Case "shift" : modifiers = modifiers Or ModShift
                Case "win", "windows", "meta" : modifiers = modifiers Or ModWin
                Case Else : Return False
            End Select
        Next
        Dim last = parts(parts.Count - 1)
        Select Case last.ToLowerInvariant()
            Case "esc" : last = "Escape"
            Case "del" : last = "Delete"
        End Select
        If last.Length = 1 AndAlso Char.IsDigit(last(0)) Then last = "D" & last
        Dim parsed As Keys
        If Not [Enum].TryParse(last, True, parsed) OrElse parsed = Keys.None OrElse
           parsed = Keys.ControlKey OrElse parsed = Keys.ShiftKey OrElse parsed = Keys.Menu OrElse (parsed And Keys.Modifiers) <> 0 Then Return False
        key = parsed
        Return modifiers <> 0
    End Function

    ''' <summary>The shortcut written the usual way ("Ctrl+Alt+Space"); "" when it is not one.</summary>
    Public Function Normalize(text As String) As String
        Dim m As UInteger, k As Keys
        If Not TryParse(text, m, k) Then Return ""
        Dim parts As New List(Of String)
        If (m And ModControl) <> 0 Then parts.Add("Ctrl")
        If (m And ModAlt) <> 0 Then parts.Add("Alt")
        If (m And ModShift) <> 0 Then parts.Add("Shift")
        If (m And ModWin) <> 0 Then parts.Add("Win")
        Dim name = k.ToString()
        If name.Length = 2 AndAlso name(0) = "D"c AndAlso Char.IsDigit(name(1)) Then name = name.Substring(1)
        parts.Add(name)
        Return String.Join("+", parts)
    End Function

    ''' <summary>
    ''' A key inside one of this app's own windows (the screenshot layer): a key alone ("Enter", "Esc", "F2", "Space") or with
    ''' Ctrl, Alt or Shift. False when it is not one.
    ''' </summary>
    Public Function TryParseKey(text As String, ByRef keyData As Keys) As Boolean
        keyData = Keys.None
        If String.IsNullOrWhiteSpace(text) Then Return False
        Dim parts = text.Split("+"c).Select(Function(p) p.Trim()).Where(Function(p) p.Length > 0).ToList()
        If parts.Count = 0 Then Return False
        Dim mods = Keys.None
        For Each p In parts.Take(parts.Count - 1)
            Select Case p.ToLowerInvariant()
                Case "ctrl", "control" : mods = mods Or Keys.Control
                Case "alt" : mods = mods Or Keys.Alt
                Case "shift" : mods = mods Or Keys.Shift
                Case Else : Return False
            End Select
        Next
        Dim last = parts(parts.Count - 1)
        Select Case last.ToLowerInvariant()
            Case "esc" : last = "Escape"
            Case "del" : last = "Delete"
            Case "enter", "return" : last = "Enter"
        End Select
        If last.Length = 1 AndAlso Char.IsDigit(last(0)) Then last = "D" & last
        Dim parsed As Keys
        If Not [Enum].TryParse(last, True, parsed) OrElse parsed = Keys.None OrElse parsed = Keys.ControlKey OrElse parsed = Keys.ShiftKey OrElse
           parsed = Keys.Menu OrElse (parsed And Keys.Modifiers) <> 0 Then Return False
        keyData = parsed Or mods
        Return True
    End Function

    ''' <summary>The key written the usual way ("Enter", "Esc", "Ctrl+F2"); "" when it is not one.</summary>
    Public Function NormalizeKey(text As String) As String
        Dim k As Keys
        If Not TryParseKey(text, k) Then Return ""
        Dim parts As New List(Of String)
        If (k And Keys.Control) <> 0 Then parts.Add("Ctrl")
        If (k And Keys.Alt) <> 0 Then parts.Add("Alt")
        If (k And Keys.Shift) <> 0 Then parts.Add("Shift")
        Dim code = k And Keys.KeyCode
        Dim name = If(code = Keys.Enter, "Enter", If(code = Keys.Escape, "Esc", code.ToString()))
        If name.Length = 2 AndAlso name(0) = "D"c AndAlso Char.IsDigit(name(1)) Then name = name.Substring(1)
        parts.Add(name)
        Return String.Join("+", parts)
    End Function

End Module
