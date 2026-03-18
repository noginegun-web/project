namespace Oxygen.Csharp.API;

public static class PermissionManager
{
    public static void GrantUserPermission(string userId, string permission)
    {
        Oxygen.Permissions.GrantUserPermission(userId, permission);
    }

    public static void RevokeUserPermission(string userId, string permission)
    {
        Oxygen.Permissions.RevokeUserPermission(userId, permission);
    }

    public static void GrantGroupPermission(string groupName, string permission)
    {
        Oxygen.Permissions.GrantGroupPermission(groupName, permission);
    }

    public static void RevokeGroupPermission(string groupName, string permission)
    {
        Oxygen.Permissions.RevokeGroupPermission(groupName, permission);
    }

    public static void AddUserToGroup(string userId, string groupName)
    {
        Oxygen.Permissions.AddUserToGroup(userId, groupName);
    }

    public static void RemoveUserFromGroup(string userId, string groupName)
    {
        Oxygen.Permissions.RemoveUserFromGroup(userId, groupName);
    }
}
