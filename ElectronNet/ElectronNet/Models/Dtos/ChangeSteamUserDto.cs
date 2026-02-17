using SteamKit2;

namespace ElectronNet.Models.Dtos;

public class ChangeSteamUserDto : SteamUser
{
    public bool? OfflineMode { get; set; }
    public EPersonaState? PersonaState { get; set; }
}