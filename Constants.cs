namespace ShoryuNet
{
    /// <summary>
    /// Defines the constants to be used in ShoryuNet
    /// </summary>
    public static class Constants
    {
        // How many states should we store in the RingBuffer for each player?
        public const int ROLLBACK_WINDOW = 20;
        // How many prior frames should be added in reserve to each GameState?
        public const int MAX_RESERVE_PAYLOADS = 2;
        // How many remote players are allowed to be in the peernet?
        public const int MAX_PLAYERS = 2;
        // How many spectators are allowed to be in the peernet?
        public const int MAX_SPECTATORS = 24;
        // By how many frames are local inputs delayed?
        public const int INPUT_DELAY = 2;
        // How many seconds from now should a game start when Start is called?
        public const int GAME_START_TIME = 2;
        // After how many seconds should we send another RequestJoin packet?
        public const int REQUEST_JOIN_RETRY_TIME = 1;
    }
}
