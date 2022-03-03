using Hearty.Common;

namespace Hearty.Server;

/// <summary>
/// Media types of promise outputs from the core Hearty 
/// server implementation, in parsed form.
/// </summary>
public static class ServedMediaTypes
{
    /// <summary>
    /// JSON in UTF-8.
    /// </summary>
    public static readonly ParsedContentType Json = "application/json";

    /// <summary>
    /// Plain text.
    /// </summary>
    public static readonly ParsedContentType TextPlain = "text/plain";

    /// <summary>
    /// MessagePack payload.
    /// </summary>
    public static readonly ParsedContentType MsgPack = "application/msgpack";

    /// <summary>
    /// <see cref="ExceptionPayload" /> serialized in JSON.
    /// </summary>
    public static readonly ParsedContentType ExceptionJson = "application/vnd.hearty.exception+json";

    /// <summary>
    /// <see cref="ExceptionPayload" /> serialized in MessagePack.
    /// </summary>
    public static readonly ParsedContentType ExceptionMsgPack = "application/vnd.hearty.exception+msgpack";

    /// <summary>
    /// Multi-part container for a promise result stream.
    /// </summary>
    public static readonly ParsedContentType MultipartParallel = "multipart/parallel; boundary=#";

    /// <summary>
    /// JSON Web Token in the compact serialization format.
    /// </summary>
    public static readonly ParsedContentType JsonWebToken = "application/jwt";

    /// <summary>
    /// XHTML.
    /// </summary>
    public static readonly ParsedContentType XHtml = "application/xhtml+xml";

    /// <summary>
    /// JSON Web Token wrapped in a JSON container.
    /// </summary>
    public static readonly ParsedContentType JsonWebTokenJson = "application/jwt+json";
}
