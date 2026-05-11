using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OpenClaw
{
    /// <summary>
    /// Result structure returned by CommandRunner execution methods.
    /// </summary>
    public sealed class RunResult
    {
        private int _exitCode;
        private string _stdout;
        private string _stderr;
        private bool _timedOut;
        private int _processId;

        /// <summary>Process exit code (0 = success).</summary>
        public int ExitCode
        {
            get { return _exitCode; }
            set { _exitCode = value; }
        }

        /// <summary>Captured standard output text.</summary>
        public string Stdout
        {
            get { return _stdout; }
            set { _stdout = value; }
        }

        /// <summary>Captured standard error text.</summary>
        public string Stderr
        {
            get { return _stderr; }
            set { _stderr = value; }
        }

        /// <summary>True if the process was killed due to timeout.</summary>
        public bool TimedOut
        {
            get { return _timedOut; }
            set { _timedOut = value; }
        }

        /// <summary>Process ID (after Start).</summary>
        public int ProcessId
        {
            get { return _processId; }
            set { _processId = value; }
        }
    }

    /// <summary>
    /// Provides command execution services with output capture, timeout,
    /// detached (fire-and-forget), and PowerShell wrapping modes.
    /// Compatible with .NET Framework 2.0 — no LINQ, no async/await, no var.
    /// </summary>
    public sealed class CommandRunner
    {
        /// <summary>
        /// Executes a command and captures its output.
        /// Supports both "cmd /c ..." and direct executable paths.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        /// <param name="timeoutMs">
        /// Timeout in milliseconds. If 0 or negative, waits indefinitely.
        /// When exceeded, the process is killed.
        /// </param>
        /// <returns>A RunResult with exit code, output, timeout status, and PID.</returns>
        public RunResult Execute(string commandLine, int timeoutMs)
        {
            if (commandLine == null)
            {
                throw new ArgumentNullException("commandLine");
            }

            string fileName;
            string arguments;
            ParseCommandLine(commandLine, out fileName, out arguments);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;


            StringBuilder stdoutBuilder = new StringBuilder(4096);
            StringBuilder stderrBuilder = new StringBuilder(4096);
            bool timedOut = false;

            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;

                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stdoutBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        stderrBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();

                int processId = process.Id;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (timeoutMs > 0)
                {
                    timedOut = !process.WaitForExit(timeoutMs);
                    if (timedOut)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                process.WaitForExit();
                            }
                        }
                        catch (InvalidOperationException)
                        {
                            // Process may have exited between check and Kill call
                        }
                        catch (Win32Exception)
                        {
                            // Insufficient privileges to kill
                        }
                    }
                }
                else
                {
                    process.WaitForExit();
                }

                // Ensure async read operations complete
                process.WaitForExit();

                RunResult result = new RunResult();
                result.ExitCode = process.ExitCode;
                result.Stdout = stdoutBuilder.ToString();
                result.Stderr = stderrBuilder.ToString();
                result.TimedOut = timedOut;
                result.ProcessId = processId;

                return result;
            }
        }

        /// <summary>
        /// Starts a process in fire-and-forget mode.
        /// Returns immediately with the process ID. No output is captured.
        /// </summary>
        /// <param name="commandLine">Command line to execute.</param>
        /// <returns>A RunResult with the process ID set; other fields are default.</returns>
        public RunResult ExecuteDetached(string commandLine)
        {
            if (commandLine == null)
            {
                throw new ArgumentNullException("commandLine");
            }

            string fileName;
            string arguments;
            ParseCommandLine(commandLine, out fileName, out arguments);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            RunResult result = new RunResult();
            result.Stdout = string.Empty;
            result.Stderr = string.Empty;
            result.TimedOut = false;
            result.ExitCode = 0;

            try
            {
                Process process = Process.Start(startInfo);
                result.ProcessId = process.Id;
                process.Close(); // Release our handle without killing the process
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.Stderr = ex.Message;
                result.ProcessId = 0;
            }

            return result;
        }

        /// <summary>
        /// Executes a command via PowerShell (-NoProfile -Command).
        /// On non-Windows platforms, falls back to cmd /c.
        /// </summary>
        /// <param name="commandLine">PowerShell command to execute.</param>
        /// <param name="timeoutMs">Timeout in milliseconds (same semantics as Execute).</param>
        /// <returns>A RunResult with captured output and exit code.</returns>
        public RunResult ExecutePowerShell(string commandLine, int timeoutMs)
        {
            if (commandLine == null)
            {
                throw new ArgumentNullException("commandLine");
            }

            bool isWindows = (Environment.OSVersion.Platform == PlatformID.Win32NT ||
                              Environment.OSVersion.Platform == PlatformID.Win32Windows);

            string wrappedCommand;
            if (isWindows)
            {
                // Wrap in PowerShell — escape embedded double-quotes
                string escapedCommand = commandLine.Replace("\"", "\\\"");
                wrappedCommand = "powershell -NoProfile -Command \"" + escapedCommand + "\"";
            }
            else
            {
                // Fallback: use cmd /c
                wrappedCommand = "cmd /c " + commandLine;
            }

            return Execute(wrappedCommand, timeoutMs);
        }

        /// <summary>
        /// Parses a command line string into an executable filename and its arguments.
        /// Handles:
        ///   - "cmd /c ..." and "cmd.exe /c ..." prefixes (extracts cmd.exe)
        ///   - Quoted executable paths ("C:\Program Files\foo.exe" --arg)
        ///   - Simple unquoted paths (notepad.exe readme.txt)
        /// </summary>
        private static void ParseCommandLine(string commandLine, out string fileName, out string arguments)
        {
            fileName = null;
            arguments = null;

            if (commandLine == null || commandLine.Length == 0)
            {
                return;
            }

            string trimmed = commandLine.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            // Detect "cmd /c ..." or "cmd.exe /c ..." prefix
            if (StartsWithIgnoreCase(trimmed, "cmd /c ") ||
                StartsWithIgnoreCase(trimmed, "cmd.exe /c "))
            {
                fileName = "cmd.exe";
                if (StartsWithIgnoreCase(trimmed, "cmd /c "))
                {
                    arguments = "/c " + trimmed.Substring(6).Trim();
                }
                else
                {
                    arguments = "/c " + trimmed.Substring(10).Trim();
                }
                return;
            }

            // Handle quoted executable path
            if (trimmed[0] == '"')
            {
                int closingQuote = trimmed.IndexOf('"', 1);
                if (closingQuote > 0)
                {
                    fileName = trimmed.Substring(1, closingQuote - 1);
                    if (closingQuote + 1 < trimmed.Length)
                    {
                        arguments = trimmed.Substring(closingQuote + 1).Trim();
                    }
                    else
                    {
                        arguments = string.Empty;
                    }
                    return;
                }
            }

            // Split by first space
            int spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex > 0)
            {
                fileName = trimmed.Substring(0, spaceIndex);
                arguments = trimmed.Substring(spaceIndex + 1).Trim();
            }
            else
            {
                fileName = trimmed;
                arguments = string.Empty;
            }
        }

        /// <summary>
        /// Case-insensitive StartsWith helper (avoids StringComparison which is .NET 2.0 SP1+).
        /// </summary>
        private static bool StartsWithIgnoreCase(string source, string prefix)
        {
            if (source.Length < prefix.Length)
            {
                return false;
            }

            for (int i = 0; i < prefix.Length; i++)
            {
                char c1 = source[i];
                char c2 = prefix[i];
                if (char.ToUpperInvariant(c1) != char.ToUpperInvariant(c2))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
