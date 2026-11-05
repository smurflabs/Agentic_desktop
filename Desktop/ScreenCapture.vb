Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Windows.Forms

''' <summary>Screenshots for the chat: the whole screen, or an area chosen by dragging (like the Snipping Tool).</summary>
Friend Module ScreenCapture

    ''' <summary>Every monitor at once, in screen pixels, with the rectangle it covers.</summary>
    Public Function CaptureVirtualScreen() As (Image As Bitmap, Bounds As Rectangle)
        Dim b = SystemInformation.VirtualScreen
        Dim bmp As New Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb)
        Using g = Graphics.FromImage(bmp)
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy)
        End Using
        Return (bmp, b)
    End Function

    ''' <summary>The part of the image inside area (in image pixels); Nothing when too small.</summary>
    Public Function Crop(image As Bitmap, area As Rectangle) As Bitmap
        Dim r = Rectangle.Intersect(area, New Rectangle(Point.Empty, image.Size))
        If r.Width < 2 OrElse r.Height < 2 Then Return Nothing
        Return image.Clone(r, image.PixelFormat)
    End Function

    ''' <summary>A smaller copy whose longest side is at most maxSide (the model needs no more; a 4K screen would cost far more), or the image itself.</summary>
    Public Function ScaleDown(image As Bitmap, maxSide As Integer) As Bitmap
        Dim longest = Math.Max(image.Width, image.Height)
        If longest <= maxSide Then Return image
        Dim f = maxSide / CDbl(longest)
        Dim small As New Bitmap(Math.Max(1, CInt(image.Width * f)), Math.Max(1, CInt(image.Height * f)), PixelFormat.Format24bppRgb)
        Using g = Graphics.FromImage(small)
            g.InterpolationMode = InterpolationMode.HighQualityBicubic
            g.SmoothingMode = SmoothingMode.HighQuality
            g.PixelOffsetMode = PixelOffsetMode.HighQuality
            g.DrawImage(image, 0, 0, small.Width, small.Height)
        End Using
        Return small
    End Function

    Public Function ToPng(image As Image) As Byte()
        Using ms As New MemoryStream()
            image.Save(ms, ImageFormat.Png)
            Return ms.ToArray()
        End Using
    End Function

    ''' <summary>The monitor under the mouse, in the screenshot's pixels (its top-left is the virtual screen's corner).</summary>
    Public Function ScreenUnderCursor(virtualBounds As Rectangle) As Rectangle
        Dim s = Screen.FromPoint(Cursor.Position).Bounds
        s.Offset(-virtualBounds.Left, -virtualBounds.Top)
        Return s
    End Function

End Module

''' <summary>
''' A full-screen layer over a screenshot: drag to choose an area (Enter - or the key set in Settings - or a double-click takes the whole monitor under the
''' mouse, Esc cancels). Area is in the screenshot's pixels.
''' </summary>
Friend NotInheritable Class RegionPicker
    Inherits Form

    Private ReadOnly _shot As Bitmap
    Private ReadOnly _virtual As Rectangle
    Private ReadOnly _shade As New SolidBrush(Color.FromArgb(125, 0, 0, 0))
    Private ReadOnly _accent As Color = Color.FromArgb(139, 150, 255)
    Private _start As Point?
    Private _now As Point

    Private _area As Rectangle?

    ''' <summary>The chosen area (Nothing when cancelled).</summary>
    Public ReadOnly Property Area As Rectangle?
        Get
            Return _area
        End Get
    End Property

    ' The keys that take the whole monitor under the mouse and that cancel (Settings → Shortcuts; Enter and Esc by default).
    Private ReadOnly _wholeScreenKey As Keys
    Private ReadOnly _cancelKey As Keys

    Public Sub New(shot As Bitmap, virtualBounds As Rectangle, Optional wholeScreenKey As Keys = Keys.Enter, Optional cancelKey As Keys = Keys.Escape)
        _shot = shot
        _virtual = virtualBounds
        _wholeScreenKey = wholeScreenKey
        _cancelKey = cancelKey
        FormBorderStyle = FormBorderStyle.None
        StartPosition = FormStartPosition.Manual
        Bounds = virtualBounds
        TopMost = True
        ShowInTaskbar = False
        DoubleBuffered = True
        KeyPreview = True
        Cursor = Cursors.Cross
        Text = "Agentic screenshot"
        BackColor = Color.Black
    End Sub

    Private ReadOnly Property Selection As Rectangle
        Get
            If Not _start.HasValue Then Return Rectangle.Empty
            Dim a = _start.Value
            Return Rectangle.FromLTRB(Math.Min(a.X, _now.X), Math.Min(a.Y, _now.Y), Math.Max(a.X, _now.X), Math.Max(a.Y, _now.Y))
        End Get
    End Property

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        Activate()
        Focus()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        g.DrawImageUnscaled(_shot, 0, 0)
        Dim sel = Selection
        Using outside As New Region(ClientRectangle)
            If sel.Width > 0 AndAlso sel.Height > 0 Then outside.Exclude(sel)
            g.FillRegion(_shade, outside)
        End Using
        g.SmoothingMode = SmoothingMode.AntiAlias
        Using font As New Font("Segoe UI", 10.5F, FontStyle.Regular, GraphicsUnit.Point)
            If sel.Width > 0 AndAlso sel.Height > 0 Then
                Using pen As New Pen(_accent, 2)
                    g.DrawRectangle(pen, sel)
                End Using
                DrawLabel(g, font, $"{sel.Width} × {sel.Height}", New Point(sel.Left, Math.Max(0, sel.Top - 30)))
            End If
            ' The hint sits at the top of the monitor under the mouse.
            Dim screenRect = ScreenCapture.ScreenUnderCursor(_virtual)
            Dim hint = "Drag to choose an area  ·  Enter: whole screen  ·  Esc: cancel"
            Dim size = g.MeasureString(hint, font)
            DrawLabel(g, font, hint, New Point(screenRect.Left + CInt((screenRect.Width - size.Width) / 2), screenRect.Top + 18))
        End Using
    End Sub

    Private Shared Sub DrawLabel(g As Graphics, font As Font, text As String, at As Point)
        Dim size = g.MeasureString(text, font)
        Dim box As New RectangleF(at.X, at.Y, size.Width + 16, size.Height + 8)
        Using bg As New SolidBrush(Color.FromArgb(225, 15, 22, 38)), fg As New SolidBrush(Color.White), path = RoundedRect(box, 7)
            g.FillPath(bg, path)
            g.DrawString(text, font, fg, at.X + 8, at.Y + 4)
        End Using
    End Sub

    Private Shared Function RoundedRect(r As RectangleF, radius As Single) As GraphicsPath
        Dim p As New GraphicsPath()
        Dim d = radius * 2
        p.AddArc(r.X, r.Y, d, d, 180, 90)
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90)
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90)
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90)
        p.CloseFigure()
        Return p
    End Function

    Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
        MyBase.OnMouseDown(e)
        If e.Button = MouseButtons.Left Then
            _start = e.Location
            _now = e.Location
            Invalidate()
        ElseIf e.Button = MouseButtons.Right Then
            Finish(Nothing)
        End If
    End Sub

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        If _start.HasValue Then
            _now = e.Location
            Invalidate()
        End If
    End Sub

    Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
        MyBase.OnMouseUp(e)
        If e.Button <> MouseButtons.Left OrElse Not _start.HasValue Then Return
        Dim sel = Selection
        If sel.Width >= 4 AndAlso sel.Height >= 4 Then
            Finish(sel)
        Else
            _start = Nothing ' a click, not a drag: keep choosing
            Invalidate()
        End If
    End Sub

    Protected Overrides Sub OnDoubleClick(e As EventArgs)
        MyBase.OnDoubleClick(e)
        Finish(ScreenCapture.ScreenUnderCursor(_virtual))
    End Sub

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        MyBase.OnKeyDown(e)
        If e.KeyData = _cancelKey Then Finish(Nothing)
        If e.KeyData = _wholeScreenKey Then Finish(ScreenCapture.ScreenUnderCursor(_virtual))
    End Sub

    Private Sub Finish(area As Rectangle?)
        _area = area
        DialogResult = If(area.HasValue, DialogResult.OK, DialogResult.Cancel)
        Close()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _shade.Dispose()
        MyBase.Dispose(disposing)
    End Sub

End Class
