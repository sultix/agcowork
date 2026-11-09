using System.Net;

namespace Agcowork.Application.Common.Security;

/// <summary>
/// Network address the current request comes from, as the host established it: the connection's address, or the one a
/// trusted proxy forwarded (never a header a client can set at will). Only for limits such as login throttling, never for
/// authorization.
/// </summary>
public interface IClientAddress
{
    /// <summary>Null when the host does not know it (in-process calls, tests).</summary>
    IPAddress? Address { get; }
}
