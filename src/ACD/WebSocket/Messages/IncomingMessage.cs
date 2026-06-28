using System.Text.Json.Serialization;

namespace ACWF.WebSocket.Messages;

/// <summary>Record discriminador usado para leer el campo "type" antes de la deserialización completa.</summary>
public sealed record BaseMessage(
    [property: JsonPropertyName("type")] string Type);

/// <summary>AUTH — intercambio de Bearer token. Debe ser el primer mensaje después de CONNECTED.</summary>
public sealed record AuthMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("token")] string Token);

/// <summary>PDF_DOWNLOAD — anuncia la recepción del PDF. El siguiente frame contiene los datos binarios.</summary>
public sealed record PdfDownloadMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")]
    string Filename,
    [property: JsonPropertyName("size")] long Size);

/// <summary>REQUEST_SIGNED_FILE — solicita el PDF firmado para enviarlo de vuelta.</summary>
public sealed record RequestSignedFileMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("filename")]
    string Filename);

/// <summary>CLEANUP_CONFIRMED — el frontend confirma que el upload al backend fue exitoso; ACWF puede borrar los PDFs.</summary>
public sealed record CleanupConfirmedMessage(
    [property: JsonPropertyName("type")] string Type);