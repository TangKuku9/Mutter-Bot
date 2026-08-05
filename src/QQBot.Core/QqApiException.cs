namespace QQBot.Core;

public class QqApiException : Exception
{
    public int ErrCode { get; }
    public string? TraceId { get; }

    public QqApiException(int errCode, string message, string? traceId = null) : base($"err_code={errCode}: {message}")
    {
        ErrCode = errCode;
        TraceId = traceId;
    }
}
