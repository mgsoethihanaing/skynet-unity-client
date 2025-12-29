// OpCodes.cs
// Protocol command definitions for Skynet client-server communication

namespace SkynetUnity
{
    /// <summary>
    /// Skynet protocol operation codes.
    /// Must match server-side definitions exactly.
    /// </summary>
    public enum OpCode : ushort
    {
        // Authentication (1xxx)
        LoginReq = 1001,
        LoginRes = 1002,
        
        // Heartbeat (2xxx)
        Ping = 2001,
        Pong = 2002,
        
        // Chat (3xxx)
        ChatReq = 3001,
        ChatPush = 3002,
        
        // Tasks/Missions (4xxx)
        TaskReq = 4001,
        TaskRes = 4002
    }
}