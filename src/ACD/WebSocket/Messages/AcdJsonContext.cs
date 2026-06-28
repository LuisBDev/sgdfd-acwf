using System.Text.Json.Serialization;

namespace ACWF.WebSocket.Messages;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BaseMessage))]
[JsonSerializable(typeof(AuthMessage))]
[JsonSerializable(typeof(PdfDownloadMessage))]
[JsonSerializable(typeof(RequestSignedFileMessage))]
[JsonSerializable(typeof(AuthOkMessage))]
[JsonSerializable(typeof(ConnectedMessage))]
[JsonSerializable(typeof(PdfReceivedMessage))]
[JsonSerializable(typeof(FirmaDisponibleMessage))]
[JsonSerializable(typeof(SignedFileMessage))]
[JsonSerializable(typeof(FirmaTimeoutMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(CleanupConfirmedMessage))]
[JsonSerializable(typeof(CleanupDoneMessage))]
public partial class AcwfJsonContext : JsonSerializerContext
{
}