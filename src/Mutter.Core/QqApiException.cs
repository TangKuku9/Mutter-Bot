namespace Mutter.Core;

public class QqApiException : Exception
{
    public int ErrCode { get; }
    public string? TraceId { get; }
    public int? HttpStatus { get; }

    public QqApiException(int errCode, string message, string? traceId = null, int? httpStatus = null)
        : base($"err_code={errCode}: {message}")
    {
        ErrCode = errCode;
        TraceId = traceId;
        HttpStatus = httpStatus;
    }
}
