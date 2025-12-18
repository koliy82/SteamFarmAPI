namespace SteamAPI.Models.Sessions
{
    public enum SessionStatus
    {
        Unknown,
        Active,
        NeedAuth,
        Stopped,
        Deleted,
        TryAnotherCM,
    }
}
