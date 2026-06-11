namespace ACWF.WebSocket.Messages;

/// <summary>
/// Constantes string para discriminación de tipos de mensaje WebSocket.
/// </summary>
public static class MessageType
{
    // Incoming (MFD → ACWF)
    public const string Auth = "AUTH";
    public const string PdfDownload = "PDF_DOWNLOAD";
    public const string RequestSignedFile = "REQUEST_SIGNED_FILE";

    // Outgoing (ACWF → MFD)
    public const string AuthOk = "AUTH_OK";
    public const string Connected = "CONNECTED";
    public const string PdfReceived = "PDF_RECEIVED";
    public const string FirmaDisponible = "FIRMA_DISPONIBLE";
    public const string SignedFile = "SIGNED_FILE";
    public const string FirmaTimeout = "FIRMA_TIMEOUT";
    public const string Error = "ERROR";
}
