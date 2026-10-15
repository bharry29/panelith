namespace OmniPanel.Monitoring.IO;

/// <summary>
/// Volume capacity probe. Separated from <see cref="IFileSystem"/> so Linux
/// system tests can fixture disk usage without calling real <see cref="DriveInfo"/>.
/// </summary>
public interface IDriveProbe
{
    /// <summary>Every mounted volume the source should expose.</summary>
    IReadOnlyList<DriveVolume> GetVolumes();
}

/// <summary>One filesystem volume.</summary>
/// <param name="Name">Stable short name used in the sensor id.</param>
/// <param name="TotalBytes">Capacity.</param>
/// <param name="UsedBytes">Bytes currently allocated.</param>
public readonly record struct DriveVolume(string Name, long TotalBytes, long UsedBytes);

/// <summary>Production probe backed by <see cref="DriveInfo"/>.</summary>
public sealed class SystemDriveProbe : IDriveProbe
{
    /// <summary>Shared instance.</summary>
    public static SystemDriveProbe Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<DriveVolume> GetVolumes()
    {
        var result = new List<DriveVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                var total = drive.TotalSize;
                if (total <= 0)
                {
                    continue;
                }

                var used = total - drive.TotalFreeSpace;
                var name = string.IsNullOrWhiteSpace(drive.Name)
                    ? "volume"
                    : drive.Name.TrimEnd('/', '\\').Replace('\\', '_').Replace('/', '_');
                if (string.IsNullOrEmpty(name))
                {
                    name = "volume";
                }

                result.Add(new DriveVolume(name, total, Math.Max(0, used)));
            }
            catch (IOException)
            {
                // Removable media can vanish between IsReady and TotalSize.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }
}
