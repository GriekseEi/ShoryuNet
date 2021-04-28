namespace ShoryuNet
{
    /// <summary>
    /// Signifies what the type of a given player is
    /// </summary>
    /// <remarks>This enum has byte as a base type to make it smaller to fit into a byte array</remarks>
    public enum PlayerType : byte
    {
        Local,
        Remote,
        Spectator
    }
}
