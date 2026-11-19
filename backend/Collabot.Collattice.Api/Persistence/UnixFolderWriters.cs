using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Collabot.Collattice.Api.Persistence;

// Says whether anyone but the user running the app can create files in a folder on macOS and Linux, so
// the default database folder can be refused when someone else could have planted a database in it.
//
// On Linux, someone else can write to a folder when it belongs to another user, when its "others" bit
// allows writing, or when its group bit does. With an access rule (ACL) on the folder, the group bits are
// the rule's mask: they cap what every named user and group in the rule may do, so a folder whose group
// bit doesn't allow writing gives no one write access through a rule either. Group write is accepted only
// when the group is the user's own private group, the one Ubuntu and other distributions give each user
// under the same name, with no one else in it, because that is what the installers' own folder is under
// a 002 umask. Any access rule on a group-writable folder is refused: what it grants isn't read.
//
// On macOS, a new folder takes the group of the folder it is created in, which in a home folder is
// "staff", every local user's group, so a group-writable folder is refused whatever its group is.
// Folder owners and access rules aren't read on macOS, so neither is checked there.
//
// Everything here only reads, so --reset-admin-key can use it too.
[UnsupportedOSPlatform("windows")]
internal static partial class UnixFolderWriters
{
    // A description of who else can write to the folder, or null when only the running user can.
    public static string? Describe(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        var mode = File.GetUnixFileMode(folder);

        if ((mode & UnixFileMode.OtherWrite) != UnixFileMode.None)
        {
            return "every user can write to it";
        }

        if (OperatingSystem.IsLinux())
        {
            return DescribeOnLinux(folder, mode);
        }

        var groupCanWrite = (mode & UnixFileMode.GroupWrite) != UnixFileMode.None;

        return groupCanWrite ? "its group can write to it" : null;
    }

    [SupportedOSPlatform("linux")]
    private static string? DescribeOnLinux(string folder, UnixFileMode mode)
    {
        if (!Native.TryGetOwner(folder, out var ownerId, out var groupId, out var errno))
        {
            return $"Collattice could not read who owns it (error {errno.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        }

        if (ownerId != Native.GetEffectiveUserId())
        {
            return "it belongs to another user";
        }

        if ((mode & UnixFileMode.GroupWrite) == UnixFileMode.None)
        {
            return null;
        }

        if (Native.HasAccessRule(folder))
        {
            return "an access rule (ACL) on it can let other users or groups write to it";
        }

        if (!Native.TryGetGroup(groupId, out var groupName, out var members))
        {
            return "its group can write to it, and Collattice could not read who is in that group";
        }

        var userName = Environment.UserName;

        var isPrivateGroup = groupId == Native.GetEffectiveGroupId()
            && string.Equals(groupName, userName, StringComparison.Ordinal)
            && members.All(member => string.Equals(member, userName, StringComparison.Ordinal));

        return isPrivateGroup ? null : $"its group '{groupName}' can write to it, and that group is not this user's own";
    }

    // The C library's own calls, with fixed layouts: statx's buffer is the same on every architecture,
    // and the leading fields of struct group are the same on every glibc.
    [SupportedOSPlatform("linux")]
    private static partial class Native
    {
        private const int _atCurrentDirectory = -100;
        private const uint _statxUid = 0x8;
        private const uint _statxGid = 0x10;
        private const int _statxSize = 256;
        private const int _statxUidOffset = 20;
        private const int _statxGidOffset = 24;
        private const int _noSuchAttribute = 61;
        private const int _notSupported = 95;
        private const int _bufferTooSmall = 34;

        public static uint GetEffectiveUserId() => geteuid();

        public static uint GetEffectiveGroupId() => getegid();

        public static bool TryGetOwner(string path, out uint ownerId, out uint groupId, out int errno)
        {
            var buffer = Marshal.AllocHGlobal(_statxSize);

            try
            {
                if (statx(_atCurrentDirectory, path, 0, _statxUid | _statxGid, buffer) != 0)
                {
                    errno = Marshal.GetLastPInvokeError();
                    ownerId = 0;
                    groupId = 0;
                    return false;
                }

                errno = 0;
                ownerId = (uint)Marshal.ReadInt32(buffer, _statxUidOffset);
                groupId = (uint)Marshal.ReadInt32(buffer, _statxGidOffset);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // A folder has an access rule when it carries the access-ACL attribute. A file system without
        // ACL support has none. Any other failure counts as a rule, so the folder is refused.
        public static bool HasAccessRule(string path) =>
            getxattr(path, "system.posix_acl_access", IntPtr.Zero, 0) >= 0
                || Marshal.GetLastPInvokeError() is not (_noSuchAttribute or _notSupported);

        public static bool TryGetGroup(uint groupId, out string name, out List<string> members)
        {
            name = string.Empty;
            members = [];

            for (var size = 1024; size <= 1024 * 1024; size *= 4)
            {
                var entry = Marshal.AllocHGlobal(Marshal.SizeOf<GroupEntry>());
                var result = Marshal.AllocHGlobal(IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    var status = getgrgid_r(groupId, entry, buffer, (nuint)size, result);

                    if (status == _bufferTooSmall)
                    {
                        continue;
                    }

                    if (status != 0 || Marshal.ReadIntPtr(result) == IntPtr.Zero)
                    {
                        return false;
                    }

                    // The entry's strings point into the buffer, which is freed only after they are read.
                    var group = Marshal.PtrToStructure<GroupEntry>(entry);

                    name = Marshal.PtrToStringUTF8(group.Name) ?? string.Empty;

                    // The member list is an array of string pointers that ends with a null one.
                    var offset = 0;
                    var member = group.Members == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(group.Members, offset);

                    while (member != IntPtr.Zero)
                    {
                        members.Add(Marshal.PtrToStringUTF8(member) ?? string.Empty);

                        offset += IntPtr.Size;
                        member = Marshal.ReadIntPtr(group.Members, offset);
                    }

                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                    Marshal.FreeHGlobal(result);
                    Marshal.FreeHGlobal(entry);
                }
            }

            return false;
        }

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int statx(int directoryFd, string path, int flags, uint mask, IntPtr buffer);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint getxattr(string path, string name, IntPtr value, nuint size);

        [LibraryImport("libc")]
        private static partial int getgrgid_r(uint groupId, IntPtr group, IntPtr buffer, nuint bufferLength, IntPtr result);

        [LibraryImport("libc")]
        private static partial uint geteuid();

        [LibraryImport("libc")]
        private static partial uint getegid();

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupEntry
        {
            public IntPtr Name;
            public IntPtr Password;
            public uint GroupId;
            public IntPtr Members;
        }
    }
}
