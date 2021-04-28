# ShoryuNet

ShoryuNet is a P2P UDP rollback netcode library written in C#. It supports multiple remote players and spectators.

## Usage
1. Build the project and import ShoryuNet.dll in your own project
1. Create a `Callbacks` struct and assign a callback to each delegate (see the `Callbacks` section for more details)
1. Create a `ShoryuBackend` object. A `ShoryuBackend` requires the following arguments:
	* The local port on which `ShoryuBackend` should send and receive messages (must range between 1024 and 65535)
	* The length of the input packets (**this must be equal across all peers**)
	* The aforementioned `Callbacks` struct
	* A `PlayerType` saying whether you are a `Local` player or a `Spectator`
	* The id byte of the local player
	* Optional: How many milliseconds reading packets needs to be delayed by to simulate latency
	* Optional: Every X packets that gets dropped to simulate packet loss
1. Call `Connect()` using the IPEndPoint of another peer
1. Call `Start()` to start the game
1. Make sure that in your update loop `AddInput(byte[] inputs)` is called with the inputs for this state as an argument, and that `SyncInput()` is called after

Example:
```c#
void EventCallback(ClientState state) {
	// Do something after the state of ShoryuBackend has changed
	...
}

void LoadState(int frame, byte[] inputs) {
	// Roll the state of the game back using the inputs given by ShoryuBackend
	...
}

Callbacks cb = new Callbacks();
cb.EventCallback += EventCallback;
cb.LoadState += LoadState;

ShoryuBackend session = new ShoryuBackend(11000, 3, cb, PlayerType.Local, 0);

session.Connect(new IPEndPoint(IPAddress.Parse("IP address of peer"), 1024);
session.Start()

void Update() {
	byte[] inputs = new byte[3];
	// Game related operations
	...
	
	session.AddInput(inputs);
	byte[] allPlayerInputs = session.SyncInput();
}
```

## Callbacks
Periodically ShoryuNet will call back the host program to alert it about changes or that you should roll back. These callbacks are as follows:
* `DebugOutput(string output)`: Prints debug info and current information about what ShoryuNet is doing, like what packets were received and sent, etc.
* `LoadState(int frame, byte[] inputs)`: Returns the framecount and the inputs for all players on that frame, which the host program should resimulate.
* `EventCallback(ClientState state)`: Returns the current state of ShoryuBackend (i.e. `NotConnected`, `WaitingForGameStart`, `InGame`, etc.) when it changes.
