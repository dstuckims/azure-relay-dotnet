namespace Microsoft.Azure.Relay.UnitTests
{
    /// <summary>Specifies the possible access rights for a user/rule.</summary>
    public enum AccessRights
    {
        /// <summary>The access right is Manage.</summary>
        Manage = 0,
        /// <summary>The access right is Send.</summary>
        Send = 1,
        /// <summary>The access right is Listen.</summary>
        Listen = 2,
    }
}