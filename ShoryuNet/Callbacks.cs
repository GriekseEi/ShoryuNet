namespace ShoryuNet
{
    /// <summary>
    /// Contains the delegates that ShoryuNet will call on occasion to notify the host program
    /// </summary>
    public struct Callbacks
    {
        // Is often called to show what ShoryuNet is doing
        public delegate void DebugOutputFn(string output);
        // Is called to tell the host program about changes to ShoryuNet's state
        public delegate void EventCallbackFn(ClientState state);
        // Is called to tell the host program to rollback to the given state
        public delegate void LoadStateFn(int frame, byte[] inputs);

        public DebugOutputFn DebugOutput;
        public EventCallbackFn EventCallback;
        public LoadStateFn LoadState;
    }
}
