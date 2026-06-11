using System.Text.Json.Serialization;

namespace ACWF.WebSocket.Messages;

/// <summary>CONNECTED — enviado inmediatamente después del WebSocket upgrade, antes que cualquier otro mensaje.</summary>
public sealed record ConnectedMessage(
    [property: JsonPropertyName("version")]
    string Version,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("watchDir")]
    string WatchDir)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.Connected;
}

/// <summary>AUTH_OK — confirma que la autenticación fue exitosa.</summary>
public sealed record AuthOkMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.AuthOk;
}

/// <summary>PDF_RECEIVED — confirma que el PDF se escribió en disco.</summary>
public sealed record PdfReceivedMessage(
    [property: JsonPropertyName("filename")]
    string Filename)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.PdfReceived;
}

/// <summary>FIRMA_DISPONIBLE — notifica que el archivo firmado está disponible.</summary>
public sealed record FirmaDisponibleMessage(
    [property: JsonPropertyName("filename")]
    string Filename)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.FirmaDisponible;
}

/// <summary>SIGNED_FILE — anuncia la transferencia binaria del archivo firmado. El siguiente frame es binario.</summary>
public sealed record SignedFileMessage(
    [property: JsonPropertyName("filename")]
    string Filename,
    [property: JsonPropertyName("expectedSize")]
    long ExpectedSize)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.SignedFile;
}

/// <summary>FIRMA_TIMEOUT — señala timeout esperando el archivo firmado.</summary>
public sealed record FirmaTimeoutMessage(
    [property: JsonPropertyName("filename")]
    string Filename,
    [property: JsonPropertyName("timeoutSeconds")]
    int TimeoutSeconds)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.FirmaTimeout;
}

/// <summary>ERROR — respuesta de error genérica.</summary>
public sealed record ErrorMessage(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")]
    string Message)
{
    [JsonPropertyName("type")] public string Type { get; init; } = MessageType.Error;
}