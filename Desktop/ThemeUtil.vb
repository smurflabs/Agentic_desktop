Imports System.Drawing

''' <summary>
''' Title bar colours that continue the Agentic web app: in dark mode the bar matches the sidebar and top bar
''' (#0d1426), in light mode the light top bar (#f8f9fc).
''' </summary>
Friend Module ThemeUtil

    Public Function Normalize(theme As String) As String
        Return If(String.Equals(theme, "light", StringComparison.OrdinalIgnoreCase), "light", "dark")
    End Function

    Public Function CaptionColor(theme As String) As Color
        Return If(Normalize(theme) = "dark", Color.FromArgb(13, 20, 38), Color.FromArgb(248, 249, 252))
    End Function

    Public Function CaptionTextColor(theme As String) As Color
        Return If(Normalize(theme) = "dark", Color.FromArgb(231, 235, 244), Color.FromArgb(15, 23, 42))
    End Function

    Public Function BorderColor(theme As String) As Color
        Return If(Normalize(theme) = "dark", Color.FromArgb(31, 42, 66), Color.FromArgb(228, 232, 239))
    End Function

    ''' <summary>Page background, shown while the web app loads (avoids a white flash in dark mode).</summary>
    Public Function PageColor(theme As String) As Color
        Return If(Normalize(theme) = "dark", Color.FromArgb(8, 12, 23), Color.FromArgb(244, 246, 250))
    End Function

    ''' <summary>
    ''' COLORREF (0x00BBGGRR) for DwmSetWindowAttribute. The channels are widened to Integer before shifting: shifting
    ''' a Byte in VB masks the count to 0-7 bits, which folded every channel into red and turned the title bar red.
    ''' </summary>
    Public Function ToColorRef(c As Color) As Integer
        Return CInt(c.R) Or (CInt(c.G) << 8) Or (CInt(c.B) << 16)
    End Function

    ''' <summary>True when Windows apps use the dark theme (HKCU ...\Themes\Personalize AppsUseLightTheme = 0).</summary>
    Public Function SystemPrefersDark() As Boolean
        Try
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                Dim v = key?.GetValue("AppsUseLightTheme")
                If TypeOf v Is Integer Then Return CInt(v) = 0
            End Using
        Catch
        End Try
        Return True
    End Function

End Module
