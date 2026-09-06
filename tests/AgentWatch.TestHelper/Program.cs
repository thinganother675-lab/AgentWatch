using System.Diagnostics;

// Development-only fixture. Never referenced by or bundled with AgentWatch.
if (args.Length == 0) return 2;
if (args[0] == "writer")
{
    Console.WriteLine("ready"); Console.Out.Flush();
    if (Console.ReadLine() != "go") return 3;
    using (var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536))
    {
        var block = new byte[65536]; Array.Fill(block, (byte)0x5A);
        for (var i = 0; i < 256; i++) output.Write(block);
        output.Flush(true);
    }
    Console.WriteLine("written"); Console.Out.Flush(); Console.ReadLine(); return 0;
}
if (args[0] == "tree")
{
    var depth = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    Process? child = null;
    try
    {
        var ids = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (depth > 0)
        {
            var start = new ProcessStartInfo(args[2]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("tree"); start.ArgumentList.Add((depth - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)); start.ArgumentList.Add(args[2]);
            child = Process.Start(start)!;
            ids += "," + await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Console.WriteLine(ids); Console.Out.Flush(); Console.ReadLine(); return 0;
    }
    finally
    {
        if (child is not null)
        {
            if (!child.HasExited) { child.StandardInput.WriteLine("exit"); if (!child.WaitForExit(5000)) child.Kill(true); }
            child.Dispose();
        }
    }
}
return 2;
