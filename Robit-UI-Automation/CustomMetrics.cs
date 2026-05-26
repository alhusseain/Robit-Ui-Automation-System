using System;
using System.Collections.Generic;
using System.Linq;

public class CustomMetrics
{
    private readonly String moduleName;
    private readonly List<long> timingsMs;

    private readonly object timingsLock;

    public CustomMetrics(String moduleName)
    {
        this.moduleName = moduleName;
        timingsLock = new object();
        timingsMs = new List<long>();

    }

    public void RecordTiming(long elapsedMs)
    {
        // if (!settings.MeasureGetClosest)
        // {
        //     return;
        // }

        lock (timingsLock)
        {
            timingsMs.Add(elapsedMs);

            if (timingsMs.Count > 100)
            {
                timingsMs.RemoveAt(0);
            }

            long min = timingsMs.Min();
            long max = timingsMs.Max();
            double avg = timingsMs.Average();

            Console.WriteLine(
                $"GetClosest: {elapsedMs} ms | avg {avg:F1} ms | min {min} ms | max {max} ms | samples {timingsMs.Count}"
            );
        }
    }



}