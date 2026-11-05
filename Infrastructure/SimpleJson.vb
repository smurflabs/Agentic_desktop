Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization

Friend Module SimpleJson

    ''' <summary>Shared options: web-safe names, tolerant reads.</summary>
    Public ReadOnly Property Options As New JsonSerializerOptions With {
        .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        .PropertyNameCaseInsensitive = True,
        .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull}

    ''' <summary>Serialize any object (anonymous types welcome) to compact JSON.</summary>
    Public Function Stringify(value As Object) As String
        Return JsonSerializer.Serialize(value, value.GetType(), Options)
    End Function

    ''' <summary>Serialize to indented JSON (config files, logs).</summary>
    Public Function StringifyPretty(value As Object) As String
        Return JsonSerializer.Serialize(value, value.GetType(), New JsonSerializerOptions With {
            .PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            .WriteIndented = True,
            .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull})
    End Function

    <Extension>
    Public Function ToJsonElement(value As Object) As JsonElement
        Dim raw = Stringify(value)
        Using doc = JsonDocument.Parse(raw)
            Return doc.RootElement.Clone()
        End Using
    End Function

End Module
