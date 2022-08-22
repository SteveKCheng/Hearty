using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Work;

/// <summary>
/// Data for an application-level heartbeat "request" in the job-submission
/// protocol.
/// </summary>
[MessagePackObject]
public struct PingMessage
{
    /// <summary>
    /// An optional human-readable message for this heartbeat request
    /// which may be displayed in logs.
    /// </summary>
    [Key("m")]
    public string? Message { get; set; }
}

/// <summary>
/// Data for the reply to an application-level "heartbeat" in the job-submission
/// protocol.
/// </summary>
[MessagePackObject]
public struct PongMessage
{
    /// <summary>
    /// An optional human-readable message for this heartbeat reply
    /// which may be displayed in logs.
    /// </summary>
    [Key("m")]
    public string? Message { get; set; }
}
