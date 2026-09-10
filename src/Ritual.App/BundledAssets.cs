using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Ritual.App;

internal static class BundledAssets
{
    public static string? Prepare()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Ritual.Assets.zip");
        if (stream is null)
            return null;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RitualChecker",
            "assets",
            hash
        );
        if (File.Exists(Path.Combine(root, ".ready")))
            return root;
        var staging = root + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                archive.ExtractToDirectory(staging);
            File.WriteAllText(Path.Combine(staging, ".ready"), hash);
            try
            {
                Directory.Move(staging, root);
            }
            catch (IOException) when (File.Exists(Path.Combine(root, ".ready"))) { }
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
        return root;
    }
}
