using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShoryuNet
{
    public enum ClientState
    {
        NotConnected,
        EstablishingConnection,
        WaitingForGameStart,
        BeginningGame,
        InGame,
        GameOver,
        RollingBack,
    }
}
