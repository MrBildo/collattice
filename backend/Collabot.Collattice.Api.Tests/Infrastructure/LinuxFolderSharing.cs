using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Collabot.Collattice.Api.Tests.Infrastructure;

// Shares a folder with other users the ways Linux allows, for the tests of the default database folder:
// through an access rule (ACL), or through a group the running user is in. Who the running user is, and
// whether their group is theirs alone, is read with the system's own commands (id, getent), never with
// the app's code, so the tests don't grade the app against itself.
[SupportedOSPlatform("linux")]
public static partial class LinuxFolderSharing
{
    // The tags and permission bits of a Linux access rule as the kernel stores it (the
    // system.posix_acl_access attribute): a version, then one entry per user or group, in tag order.
    private const ushort _userObject = 0x01;
    private const ushort _user = 0x02;
    private const ushort _groupObject = 0x04;
    private const ushort _mask = 0x10;
    private const ushort _other = 0x20;
    private const ushort _readWriteExecute = 7;
    private const ushort _readExecute = 5;
    private const uint _noId = uint.MaxValue;

    private static readonly Lazy<Account> _account = new(ReadAccount);

    public static string UserName => _account.Value.User;

    // Whether the user's primary group has the user's own name and no other members: Ubuntu's private
    // user group, the group a folder made under a 002 umask belongs to.
    public static bool PrimaryGroupIsPrivate => _account.Value.GroupIsPrivate;

    // A group the user is in that is not theirs alone, if there is one: one a folder can be given to.
    public static (uint Id, string Name)? SharedGroup => _account.Value.GroupNotItsOwn;

    // Gives another user (nobody, 65534) read, write and enter rights on the folder, the way
    // 'setfacl -m u:nobody:rwx' would, without needing the acl tools installed.
    public static void LetAnotherUserWrite(string folder)
    {
        var entries = new (ushort Tag, ushort Permissions, uint Id)[]
        {
            (_userObject, _readWriteExecute, _noId),
            (_user, _readWriteExecute, 65534),
            (_groupObject, _readExecute, _noId),
            (_mask, _readWriteExecute, _noId),
            (_other, _readExecute, _noId),
        };

        using var rule = new MemoryStream();
        using var writer = new BinaryWriter(rule);

        writer.Write(2u);

        foreach (var (tag, permissions, id) in entries)
        {
            writer.Write(tag);
            writer.Write(permissions);
            writer.Write(id);
        }

        writer.Flush();

        var bytes = rule.ToArray();

        if (SetExtendedAttribute(folder, "system.posix_acl_access", bytes, (nuint)bytes.Length, 0) != 0)
        {
            throw new InvalidOperationException($"setxattr on '{folder}' failed with error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    public static void GiveToGroup(string folder, uint groupId)
    {
        if (ChangeOwner(folder, _noId, groupId) != 0)
        {
            throw new InvalidOperationException($"chown of '{folder}' to group {groupId.ToString(CultureInfo.InvariantCulture)} failed with error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static Account ReadAccount()
    {
        var userName = Run("id", "-un");
        var primaryGroupId = Run("id", "-g");
        var groupIds = Run("id", "-G").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // getent group prints name:password:id:member,member, and nothing for a group with no entry,
        // which the app can't name either: such a group is not anyone's own.
        string[]? Entry(string groupId) => TryRun("getent", "group", groupId)?.Split(':');

        bool IsPrivate(string groupId) =>
            Entry(groupId) is [var name, _, _, var members]
                && string.Equals(name, userName, StringComparison.Ordinal)
                && string.Equals(groupId, primaryGroupId, StringComparison.Ordinal)
                && members.Split(',', StringSplitOptions.RemoveEmptyEntries).All(member => string.Equals(member, userName, StringComparison.Ordinal));

        var notItsOwn = groupIds
            .Where(groupId => !IsPrivate(groupId) && Entry(groupId) is not null)
                .Select(groupId => (Id: uint.Parse(groupId, CultureInfo.InvariantCulture), Name: Entry(groupId)![0]))
                    .FirstOrDefault();

        return new Account(userName, IsPrivate(primaryGroupId), notItsOwn.Name is null ? null : notItsOwn);
    }

    private static string Run(string command, params string[] arguments) =>
        TryRun(command, arguments)
            ?? throw new InvalidOperationException($"'{command} {string.Join(' ', arguments)}' failed.");

    private static string? TryRun(string command, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;

        var output = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        return process.ExitCode == 0 ? output.Trim() : null;
    }

    [LibraryImport("libc", EntryPoint = "setxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetExtendedAttribute(string path, string name, byte[] value, nuint size, int flags);

    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ChangeOwner(string path, uint ownerId, uint groupId);

    private sealed record Account(string User, bool GroupIsPrivate, (uint Id, string Name)? GroupNotItsOwn);
}
