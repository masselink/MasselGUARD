using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Executes pre/post tunnel scripts.
    /// Supports file paths (.bat / .ps1) and @embed:... inline scripts.
    /// Returns (exitCode, stdout+stderr). No UI references.
    /// </summary>
    public class ScriptService
    {
        private const string EmbedPrefix = "@embed:";

        public record ScriptResult(int ExitCode, string Output);

        // UTF-8 without BOM — cmd.exe chokes on BOM at the start of a .bat file.
        private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Strips a leading UTF-8 BOM (U+FEFF) if present.</summary>
        private static string StripBom(string s) =>
            s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

        public ScriptResult Run(string scriptValue, string hookName, string tunnelName)
        {
            if (string.IsNullOrWhiteSpace(scriptValue))
                return new(0, "");

            string? tempFile = null;
            string  path;
            string  ext;

            // Resolve embedded scripts to a temp file
            if (scriptValue.StartsWith(EmbedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string content = StripBom(scriptValue[EmbedPrefix.Length..]);
                ext      = content.TrimStart().StartsWith("#!") ? ".ps1" : ".bat";
                tempFile = Path.Combine(Path.GetTempPath(),
                    $"masselguard_{hookName}_{tunnelName}{ext}");
                // Write without BOM — cmd.exe interprets BOM as literal characters
                File.WriteAllText(tempFile, content, NoBomUtf8);
                path = tempFile;
            }
            else
            {
                path = scriptValue.Trim();
                ext  = Path.GetExtension(path).ToLowerInvariant();

                // For .bat files from disk: strip BOM if present by routing through a
                // clean temp file.  cmd.exe does not understand UTF-8 BOM and would pass
                // the raw bytes as the first characters of the first command.
                if (ext == ".bat" && File.Exists(path))
                {
                    try
                    {
                        string batContent = StripBom(File.ReadAllText(path, Encoding.UTF8));
                        tempFile = Path.Combine(Path.GetTempPath(),
                            $"masselguard_{hookName}_{tunnelName}.bat");
                        File.WriteAllText(tempFile, batContent, NoBomUtf8);
                        path = tempFile;
                    }
                    catch { /* fall through and run original path */ }
                }
            }

            try
            {
                var psi = ext == ".ps1"
                    ? new ProcessStartInfo("powershell.exe",
                        $"-ExecutionPolicy Bypass -NonInteractive -File \"{path}\"")
                    : new ProcessStartInfo("cmd.exe", $"/c \"{path}\"");

                psi.UseShellExecute        = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError  = true;
                psi.CreateNoWindow         = true;

                using var proc = Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                string combined = (stdout + stderr).Trim();
                return new(proc.ExitCode, combined);
            }
            catch (Exception ex)
            {
                return new(-1, ex.Message);
            }
            finally
            {
                if (tempFile != null && File.Exists(tempFile))
                    try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
