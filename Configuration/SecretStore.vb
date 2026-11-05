Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

''' <summary>
''' Keeps the API key: on Windows protected with DPAPI (CurrentUser scope) in %APPDATA%\AgenticClient\key.bin; on Linux
''' in ~/.config/AgenticClient/key, a file only this account can read (0600). Falls back gracefully (returns False) so
''' settings can still be saved to the JSON file.
''' </summary>
Friend Module SecretStore

    Private ReadOnly KeyPath As String = Path.Combine(PathsUtil.AppDataRoot(), "key.bin")
    Private ReadOnly KeyFile As String = Path.Combine(PathsUtil.AppDataRoot(), "key")

    Public Function SaveKey(plainKey As String) As Boolean
        If String.IsNullOrEmpty(plainKey) Then Return False
        If Not OperatingSystem.IsWindows() Then
            Try
                PathsUtil.EnsureDir(Path.GetDirectoryName(KeyFile))
                ' Created private before the key is written into it.
                Using f As New FileStream(KeyFile, New FileStreamOptions With {.Mode = FileMode.Create, .Access = FileAccess.Write,
                                                                               .UnixCreateMode = UnixFileMode.UserRead Or UnixFileMode.UserWrite})
                    Dim bytes = Encoding.UTF8.GetBytes(plainKey)
                    f.Write(bytes, 0, bytes.Length)
                End Using
                PlatformUtil.MakePrivate(KeyFile)
                Return True
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return False
            End Try
        End If
        Try
            PathsUtil.EnsureDir(Path.GetDirectoryName(KeyPath))
            Dim entropy = Encoding.UTF8.GetBytes("AgenticClient:v1")
            Dim protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainKey), entropy, DataProtectionScope.CurrentUser)
            File.WriteAllBytes(KeyPath, protectedBytes)
            Return True
        Catch
            Return False
        End Try
    End Function

    Public Function LoadKey() As String
        If Not OperatingSystem.IsWindows() Then
            Try
                Return If(File.Exists(KeyFile), File.ReadAllText(KeyFile, Encoding.UTF8).Trim(), Nothing)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End If
        Try
            If Not File.Exists(KeyPath) Then Return Nothing
            Dim entropy = Encoding.UTF8.GetBytes("AgenticClient:v1")
            Dim bytes = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), entropy, DataProtectionScope.CurrentUser)
            Return Encoding.UTF8.GetString(bytes)
        Catch
            Return Nothing
        End Try
    End Function

    Public Sub ClearKey()
        Try
            If File.Exists(KeyPath) Then File.Delete(KeyPath)
            If File.Exists(KeyFile) Then File.Delete(KeyFile)
        Catch
        End Try
    End Sub

End Module
