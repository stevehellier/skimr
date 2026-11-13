namespace Skimr.Infrastructure.Identity;

public enum RegistrationMode
{
    /// <summary>Anyone who can reach the server can create an account.</summary>
    Open,

    /// <summary>Sign-up needs an invite link from an admin.</summary>
    InviteOnly,

    /// <summary>Only admins create accounts.</summary>
    Closed,
}
