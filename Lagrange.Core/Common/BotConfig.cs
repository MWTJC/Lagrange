using Lagrange.Core.Utility.Sign;

namespace Lagrange.Core.Common;

/// <summary>
/// Configuration for The bot client
/// </summary>
[Serializable]
public class BotConfig
{
    /// <summary>
    /// The protocol for the client, default is Linux
    /// </summary>
    public Protocols Protocol { get; set; } = Protocols.Linux;

    /// <summary>
    /// Auto reconnect to server when disconnected
    /// </summary>
    public bool AutoReconnect { get; set; } = true;
    
    /// <summary>
    /// Use the IPv6 to connect to server, only if your network support IPv6
    /// </summary>
    public bool UseIPv6Network { get; set; } = true;
    
    /// <summary>
    /// Get optimum server from Tencent MSF server, set to false to use hardcode server
    /// </summary>
    public bool GetOptimumServer { get; set; } = true;

    /// <summary>
    /// Custom Sign Provider
    /// </summary>
    public SignProvider? CustomSignProvider { get; set; } = null;
}

/// <summary>
/// The Protocol for the client
/// </summary>
public enum Protocols
{
    Windows = 0,
    MacOs = 1,
    Linux = 2
}
