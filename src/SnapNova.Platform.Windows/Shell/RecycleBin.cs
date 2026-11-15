using Microsoft.VisualBasic.FileIO;

namespace SnapNova.Platform.Windows.Shell;

/// <summary>
/// Windows recycle bin (optional instead of SnapNova's own). Only for local drives: on network shares Windows deletes
/// permanently without asking – there the caller falls back to SnapNova's recycle bin.
/// </summary>
public static class RecycleBin
{
    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Moves the file into the Windows recycle bin. False: not possible here (network path, cancelled).</summary>
    public static bool TrySend(string path)
    {
        if (!File.Exists(path) || IsNetworkPath(path))
        {
            return false;
        }

        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            return !File.Exists(path);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
