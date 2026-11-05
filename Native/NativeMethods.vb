Imports System.Runtime.InteropServices

Namespace Win32

    ''' <summary>DWM window attributes for the title bar (dark mode from Windows 10 1809, colours from Windows 11).</summary>
    Public Enum DwmWindowAttribute As Integer
        DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20
        DWMWA_BORDER_COLOR = 34
        DWMWA_CAPTION_COLOR = 35
        DWMWA_TEXT_COLOR = 36
    End Enum

    Friend Module NativeMethods

        <DllImport("dwmapi.dll")>
        Public Function DwmSetWindowAttribute(hwnd As IntPtr, attr As DwmWindowAttribute, ByRef value As Integer, sizeOfValue As Integer) As Integer
        End Function

        <DllImport("user32.dll")>
        Private Function SetWindowPos(hwnd As IntPtr, after As IntPtr, x As Integer, y As Integer, cx As Integer, cy As Integer, flags As UInteger) As Boolean
        End Function

        <DllImport("user32.dll")>
        Private Function SendMessage(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        <DllImport("user32.dll")>
        Private Function GetForegroundWindow() As IntPtr
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Function RegisterHotKey(hwnd As IntPtr, id As Integer, modifiers As UInteger, vk As UInteger) As Boolean
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Public Function UnregisterHotKey(hwnd As IntPtr, id As Integer) As Boolean
        End Function

        <DllImport("user32.dll")>
        Public Function SetForegroundWindow(hwnd As IntPtr) As Boolean
        End Function

        <StructLayout(LayoutKind.Sequential)>
        Private Structure FLASHWINFO
            Public cbSize As UInteger
            Public hwnd As IntPtr
            Public dwFlags As UInteger
            Public uCount As UInteger
            Public dwTimeout As UInteger
        End Structure

        <DllImport("user32.dll")>
        Private Function FlashWindowEx(ByRef info As FLASHWINFO) As Boolean
        End Function

        ''' <summary>Flashes the taskbar button until the window comes to the front (something waits for the user).</summary>
        Public Sub FlashUntilFocused(hwnd As IntPtr)
            If GetForegroundWindow() = hwnd Then Return
            Const FLASHW_ALL As UInteger = 3UI, FLASHW_TIMERNOFG As UInteger = 12UI
            Dim info As New FLASHWINFO With {.hwnd = hwnd, .dwFlags = FLASHW_ALL Or FLASHW_TIMERNOFG}
            info.cbSize = CUInt(Marshal.SizeOf(info))
            FlashWindowEx(info)
        End Sub

        ''' <summary>Dark/light title bar plus caption, text and border colours. Older Windows versions ignore what they lack.</summary>
        Public Sub ApplyTitleBar(hwnd As IntPtr, dark As Boolean, caption As Integer, text As Integer, border As Integer)
            Dim flag = If(dark, 1, 0)
            If DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE, flag, 4) <> 0 Then
                DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, flag, 4)
            End If
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_CAPTION_COLOR, caption, 4)
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_TEXT_COLOR, text, 4)
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.DWMWA_BORDER_COLOR, border, 4)
            ' Windows 10 only repaints the frame when it changes or the window is (de)activated: do both.
            Const SWP_NOSIZE As UInteger = &H1UI, SWP_NOMOVE As UInteger = &H2UI, SWP_NOZORDER As UInteger = &H4UI, SWP_NOACTIVATE As UInteger = &H10UI, SWP_FRAMECHANGED As UInteger = &H20UI
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE Or SWP_NOMOVE Or SWP_NOZORDER Or SWP_NOACTIVATE Or SWP_FRAMECHANGED)
            Const WM_NCACTIVATE As Integer = &H86
            Dim active = GetForegroundWindow() = hwnd
            SendMessage(hwnd, WM_NCACTIVATE, New IntPtr(If(active, 0, 1)), IntPtr.Zero)
            SendMessage(hwnd, WM_NCACTIVATE, New IntPtr(If(active, 1, 0)), IntPtr.Zero)
        End Sub

    End Module

End Namespace
