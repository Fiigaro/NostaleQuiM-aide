using System.Text;

namespace NosSmoothCustomClient.Client;

/// <summary>
/// Renders the process scan so a wrong detection can be diagnosed from the output alone.
/// </summary>
public static class ProcessScanReport
{
    /// <summary>
    /// Scans the running processes and renders the result.
    /// </summary>
    /// <returns>The report.</returns>
    public static string Render()
    {
        var verdicts = NosTaleProcessScanner.Scan();

        try
        {
            var builder = new StringBuilder();
            var clients = verdicts.Where(v => v.IsClient).ToList();

            builder.AppendLine($"Scanned {verdicts.Count} process(es). Detected {clients.Count} NosTale client(s).");
            builder.AppendLine($"Working directory: {Environment.CurrentDirectory}");
            builder.AppendLine();

            if (clients.Count > 0)
            {
                builder.AppendLine("DETECTED AS CLIENT");
                foreach (var verdict in clients)
                {
                    builder.AppendLine($"  {verdict.Process.ProcessName,-28} pid {verdict.Process.Id,-7} {verdict.ExecutablePath}");
                }

                builder.AppendLine();
            }

            builder.AppendLine("REJECTED, GROUPED BY REASON");
            var byReason = verdicts
                .Where(v => !v.IsClient)
                .GroupBy(v => v.Reason)
                .OrderByDescending(g => g.Count());

            foreach (var group in byReason)
            {
                builder.AppendLine($"  {group.Count(),4}  {group.Key}");
            }

            builder.AppendLine();
            builder.AppendLine("PROCESSES WHOSE NAME LOOKS LIKE A GAME CLIENT");

            var interesting = verdicts
                .Where(v => Hints.Any(h => v.Process.ProcessName.Contains(h, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (interesting.Count == 0)
            {
                builder.AppendLine("  (none)");
            }

            foreach (var verdict in interesting)
            {
                builder.AppendLine($"  {verdict.Process.ProcessName,-28} pid {verdict.Process.Id,-7} {verdict.Reason}");
                builder.AppendLine($"  {string.Empty,-28}         {verdict.ExecutablePath ?? "(path unreadable)"}");
            }

            return builder.ToString();
        }
        finally
        {
            foreach (var verdict in verdicts)
            {
                verdict.Process.Dispose();
            }
        }
    }

    private static readonly string[] Hints = { "nos", "tale", "game", "client", "launcher" };
}
