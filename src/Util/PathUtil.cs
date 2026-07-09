using System;
using System.IO;
using BepInEx;

namespace StudentAge.QQAIMoments.Util
{
    internal static class PathUtil
    {
        internal static string ConfigRelative(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return Paths.ConfigPath;
            }
            if (Path.IsPathRooted(relative))
            {
                return relative;
            }
            return Path.Combine(Paths.ConfigPath, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        internal static void EnsureParent(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        internal static string SafeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "default";
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }
            return value;
        }
    }
}

