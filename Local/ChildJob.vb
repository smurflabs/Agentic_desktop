Imports System.Runtime.InteropServices

''' <summary>
''' Windows: the processes the app starts for the user (the Run button's dev servers and tests, background commands and
''' monitors) belong to a job that ends with the app, so none keeps running after the app quits, is ended in Task Manager or
''' crashes. Processes they start join the job too. Elsewhere this does nothing (the app stops them when it closes).
''' </summary>
Friend NotInheritable Class ChildJob

    Private Shared ReadOnly Job As IntPtr = Create()

    Private Sub New()
    End Sub

    ''' <summary>Adds a started process to the job (nothing happens outside Windows or when Windows refuses).</summary>
    Public Shared Sub Add(proc As Process)
        If Job = IntPtr.Zero OrElse proc Is Nothing Then Return
        Try
            AssignProcessToJobObject(Job, proc.Handle)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            ' It has already ended.
        End Try
    End Sub

    Private Shared Function Create() As IntPtr
        If Not OperatingSystem.IsWindows() Then Return IntPtr.Zero
        Try
            Dim handle = CreateJobObject(IntPtr.Zero, Nothing)
            If handle = IntPtr.Zero Then Return IntPtr.Zero
            Dim info As New JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            Dim size = Marshal.SizeOf(Of JOBOBJECT_EXTENDED_LIMIT_INFORMATION)()
            Dim buffer = Marshal.AllocHGlobal(size)
            Try
                Marshal.StructureToPtr(info, buffer, False)
                If Not SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, CUInt(size)) Then
                    CloseHandle(handle)
                    Return IntPtr.Zero
                End If
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
            ' The handle stays open while the app runs; Windows ends the job's processes when it closes with the app.
            Return handle
        Catch ex As Exception When TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is EntryPointNotFoundException
            Return IntPtr.Zero
        End Try
    End Function

    Private Const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE As UInteger = &H2000UI
    Private Const JobObjectExtendedLimitInformation As Integer = 9

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public PerProcessUserTimeLimit As Long
        Public PerJobUserTimeLimit As Long
        Public LimitFlags As UInteger
        Public MinimumWorkingSetSize As UIntPtr
        Public MaximumWorkingSetSize As UIntPtr
        Public ActiveProcessLimit As UInteger
        Public Affinity As UIntPtr
        Public PriorityClass As UInteger
        Public SchedulingClass As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure IO_COUNTERS
        Public ReadOperationCount As ULong
        Public WriteOperationCount As ULong
        Public OtherOperationCount As ULong
        Public ReadTransferCount As ULong
        Public WriteTransferCount As ULong
        Public OtherTransferCount As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        Public BasicLimitInformation As JOBOBJECT_BASIC_LIMIT_INFORMATION
        Public IoInfo As IO_COUNTERS
        Public ProcessMemoryLimit As UIntPtr
        Public JobMemoryLimit As UIntPtr
        Public PeakProcessMemoryUsed As UIntPtr
        Public PeakJobMemoryUsed As UIntPtr
    End Structure

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function CreateJobObject(attributes As IntPtr, name As String) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetInformationJobObject(job As IntPtr, infoClass As Integer, info As IntPtr, length As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function AssignProcessToJobObject(job As IntPtr, process As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(handle As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function TerminateJobObject(job As IntPtr, exitCode As UInteger) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    ''' <summary>
    ''' A job of one command's own (nested in the app's job, so it still ends with the app): everything the command starts
    ''' joins it, so Terminate stops what is left even when the shell (or a process in between) has already ended - a
    ''' process tree cannot be followed through those. Zero outside Windows or when Windows refuses.
    ''' </summary>
    Public Shared Function ForCommand(proc As Process) As IntPtr
        If Job = IntPtr.Zero OrElse proc Is Nothing Then Return IntPtr.Zero
        Dim own = IntPtr.Zero
        Try
            own = CreateJobObject(IntPtr.Zero, Nothing)
            If own = IntPtr.Zero Then Return IntPtr.Zero
            ' Already in the app's job: this empty one becomes nested in it.
            If AssignProcessToJobObject(own, proc.Handle) Then Return own
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
            ' It has already ended.
        End Try
        If own <> IntPtr.Zero Then CloseHandle(own)
        Return IntPtr.Zero
    End Function

    ''' <summary>Stops every process of a command's job and lets go of it.</summary>
    Public Shared Sub Terminate(commandJob As IntPtr)
        If commandJob = IntPtr.Zero Then Return
        TerminateJobObject(commandJob, 1UI)
        CloseHandle(commandJob)
    End Sub

    ''' <summary>Lets go of a command's job without stopping anything (what is left still ends with the app).</summary>
    Public Shared Sub Release(commandJob As IntPtr)
        If commandJob <> IntPtr.Zero Then CloseHandle(commandJob)
    End Sub

End Class
