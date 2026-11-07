using System.IO;
using System.Text.RegularExpressions;
namespace Ripley;

public static class FileNames
{
    public static string WithPrefix(string prefix, string original, FolderContext context)
    {
        Validate(original);
        var name = FolderNames.ExpandTokens(prefix, context, "filename") + original;
        Validate(name);
        if(name.Length > 240) throw new IOException("The prefixed filename is too long. Shorten the prefix to leave room for collision suffixes.");
        return name;
    }

    public static void Validate(string name)
    {
        if(string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Length > 255 ||
            Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("The filename must be a valid Windows filename. Use the folder format for subfolders.");
    }
}
