namespace ACD.WebSocket.Messages;

public static class ErrorCategory
{
    public const string UserActionable = "USER_ACTIONABLE";
    public const string Transient = "TRANSIENT";
    public const string System = "SYSTEM";
}

public static class ErrorCatalog
{
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string InvalidFilename = "INVALID_FILENAME";
    public const string MissingFirmaTipo = "MISSING_FIRMA_TIPO";
    public const string UnsupportedFirmaTipo = "UNSUPPORTED_FIRMA_TIPO";
    public const string WriteFailed = "WRITE_FAILED";
    public const string FirmaLaunchFailed = "FIRMA_LAUNCH_FAILED";
    public const string ProcessStartFailed = "PROCESS_START_FAILED";
    public const string FileLocked = "FILE_LOCKED";
    public const string ReadFailed = "READ_FAILED";
    public const string UnexpectedMessage = "UNEXPECTED_MESSAGE";
    public const string UnknownMessageType = "UNKNOWN_MESSAGE_TYPE";
    public const string InternalError = "INTERNAL_ERROR";

    private static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        [AuthRequired] = ErrorCategory.System,
        [InvalidFilename] = ErrorCategory.System,
        [MissingFirmaTipo] = ErrorCategory.System,
        [UnsupportedFirmaTipo] = ErrorCategory.System,
        [WriteFailed] = ErrorCategory.Transient,
        [FirmaLaunchFailed] = ErrorCategory.UserActionable,
        [ProcessStartFailed] = ErrorCategory.UserActionable,
        [FileLocked] = ErrorCategory.UserActionable,
        [ReadFailed] = ErrorCategory.Transient,
        [UnexpectedMessage] = ErrorCategory.System,
        [UnknownMessageType] = ErrorCategory.System,
        [InternalError] = ErrorCategory.System,
    };

    public static string CategoryOf(string code) =>
        Categories.TryGetValue(code, out var category) ? category : ErrorCategory.System;
}
